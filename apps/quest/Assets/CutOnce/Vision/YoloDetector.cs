using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.InferenceEngine;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// YOLOv9 on the passthrough camera, adapted from Meta's MultiObjectDetection sample
    /// (SentisInferenceRunManager). The bundled FP32 export preserves Meta's output graph and
    /// tensor layout from the pinned original ONNX without the failing weight quantization; output
    /// readback is guarded against failures and stalls, and NMS keeps overlapping different classes.
    ///
    /// Deliberate departures from the sample:
    ///  - its RunInference() opens with a local DllImport of ovrp_GetNodePoseStateAtTime, an
    ///    undocumented P/Invoke into OVRPlugin, purely to decide whether the camera pose is
    ///    trustworthy. We use the public camera metadata and documented asynchronous snapshot
    ///    route. This avoids immediate-Blit previous-frame pixels, but hardware pairing still
    ///    needs a real moving-head test; IsUpdatedThisFrame alone does not prove pose accuracy.
    ///  - the sample drives a world-space uGUI canvas; we raise an event and let the rest of the
    ///    pipeline decide what to do with it.
    ///  - the sample suppresses boxes across classes. We suppress duplicates within a class so a
    ///    recognized table and an object on the table can both survive.
    ///  - readback requests are polled before creating CPU clones, and a stalled request is drained
    ///    before the same worker can process another frame.
    ///
    /// Output readback is polled across frames with `yield return null` until it completes.
    /// Preprocessing, initial model warm-up and layer scheduling still need headset frame-time
    /// measurement; asynchronous output readback alone is not a smoothness guarantee.
    /// </summary>
    public class YoloDetector : MonoBehaviour
    {
        [Header("Model (FP32 YOLOv9 with Meta's output graph + COCO labels)")]
        public ModelAsset modelAsset;
        public TextAsset labelsAsset;

        [Tooltip("CPU is the recorded-photo-tested backend for this FP32 export on the Mac. Quest CPU performance and alternative GPU backends still require device measurements.")]
        public BackendType backend = BackendType.CPU;

        [Range(0f, 1f)] public float scoreThreshold = 0.35f;
        [Range(0f, 1f)] public float iouThreshold = 0.5f;

        [Tooltip("Report and discard a stalled frame after this many seconds. Wait asynchronously for its output before reusing the worker, then resume scanning.")]
        public float readbackTimeoutSeconds = 8f;

        [Tooltip("Upper bound on inference rate. The model takes as long as it takes; this only stops us queueing faster than that. A few a second keep the labels live without cooking an XR2.")]
        public float maxInferencesPerSecond = 8f;

        [Tooltip("Discard LIVE camera results older than this many unscaled seconds since texture acquisition. This is a lower bound on sensor age. Defaults to the surface paint's 0.5-second fresh window; recorded-photo DetectOnce is exempt. Increasing this can paint stale locations.")]
        [Min(0.01f)] public float maxLiveResultAgeSeconds = ObjectVisualizer.SurfaceFadeStartsAfterSeconds;

        [Tooltip("The bundled Meta YOLO model already outputs corners (x1,y1,x2,y2). Turn off only for a replacement model that outputs centre+size.")]
        public bool cornerBoxes = true;

        [Tooltip("Diagnostic-only extra pass over raw model outputs. Leave off on the headset; recorded-photo proof enables it to explain missing classes.")]
        public bool collectRawClassSummaries;

        /// <summary>No inference while this is set; the loop keeps waiting, so it can be turned back on.</summary>
        public bool Paused
        {
            get => _paused;
            set
            {
                if (_paused == value) return;
                _paused = value;
                unchecked { _publicationGeneration++; }
                _publicationCadence.Reset();
                _camera?.InvalidateSnapshots();
            }
        }

        private bool _loggedRawBox;
        private bool _paused;
        private int _publicationGeneration;
        private bool _applicationPaused;

        /// <summary>Detections, plus the camera pose and input size they were computed against.</summary>
        public event Action<List<DetectedObject>, Pose, Vector2> OnDetections;

        /// <summary>The same accepted batch with acquisition timing for live tracking; the legacy event remains available to recorded-photo callers.</summary>
        public event Action<List<DetectedObject>, Pose, Vector2, DetectionFrameTiming> OnDetectionFrame;

        // ---- diagnostics, so a dead model can never masquerade as a working one ----
        public bool ModelLoaded { get; private set; }
        public bool InferenceRunning { get; private set; }
        public float LastInferenceMs { get; private set; }
        /// <summary>Successful result publication cadence, not 1000 / processing latency. Zero means unknown or idle.</summary>
        public float InferencesPerSecond => Paused ? 0f : _publicationCadence.Read(Time.realtimeSinceStartupAsDouble);
        public int LastRawDetections { get; private set; }
        public int LastAcceptedDetections { get; private set; }
        public int TotalInferences { get; private set; }
        public string LastError { get; private set; } = "";
        public DetectionPublicationRejection LastPublicationRejection { get; private set; }
        public int DiscardedResults { get; private set; }
        public Vector2Int ModelInputSize => _inputSize;
        public YoloLetterboxLayout LastLetterboxLayout => _letterbox.Layout;

        /// <summary>
        /// Scores from the model head before thresholding or NMS. The converted model retains only
        /// the winning class for each anchor, so these are not the other 79 class logits per anchor.
        /// A copy is returned per list item; callers cannot change the detector's stored summaries.
        /// </summary>
        public readonly struct RawClassSummary
        {
            public readonly int classId, candidateCount, aboveThreshold;
            public readonly string className;
            public readonly float maxScore;

            public RawClassSummary(int classId, string className, int candidateCount, int aboveThreshold, float maxScore)
            {
                this.classId = classId;
                this.className = className;
                this.candidateCount = candidateCount;
                this.aboveThreshold = aboveThreshold;
                this.maxScore = maxScore;
            }
        }

        public IReadOnlyList<RawClassSummary> LastRawClassSummaries => _rawClassSummaries;
        public float LastRawClassThreshold { get; private set; }

        private Worker _worker;
        private Tensor<float> _input;
        private Vector2Int _inputSize;
        private Vector2Int _sourceSize;
        private readonly YoloLetterbox _letterbox = new YoloLetterbox();
        private string[] _labels;
        private VisionCamera _camera;
        private readonly List<(int classId, Vector4 box, float score)> _nmsResults = new();
        private readonly List<DetectedObject> _detections = new();
        private float _lastInferenceStartedAt = -999f;
        private readonly PublicationCadence _publicationCadence = new PublicationCadence();
        private RawClassSummary[] _rawClassSummaries = Array.Empty<RawClassSummary>();
        private VisionInferenceOperation _automaticOperation, _activeOperation, _retirementOperation;
        private bool _destroyRequested, _resourcesDisposed, _backendScheduled, _applicationQuitting, _backendCompletionUnknown;
        private VisionCamera _snapshotAwaitingDrain;
        private Tensor _drainBoxes, _drainClasses, _drainScores;
        private bool _drainRequested;

        private void Awake()
        {
            _camera = GetComponent<VisionCamera>() ?? FindAnyObjectByType<VisionCamera>();


        }

        /// <summary>
        /// Loads in Start, not Awake, and this matters: AddComponent runs Awake synchronously, so a
        /// caller doing `AddComponent<YoloDetector>().modelAsset = x` has not assigned anything yet
        /// when Awake fires. Start runs after the whole Awake pass, by which time we are configured.
        /// Safe to call again; it no-ops once loaded.
        /// </summary>
        public void LoadModel()
        {
            if (ModelLoaded) return;
            if (modelAsset == null) { Fail("no model asset assigned (expected yolov9sentis.sentis)"); return; }
            if (labelsAsset == null) { Fail("no labels asset assigned (expected SentisYoloClasses.txt)"); return; }

            _labels = labelsAsset.text.Split('\n');
            for (var i = 0; i < _labels.Length; i++) _labels[i] = NormalizeClassName(_labels[i]);
            _rawClassSummaries = new RawClassSummary[_labels.Length];

            try
            {
                var model = ModelLoader.Load(modelAsset);
                var shape = model.inputs[0].shape;
                _inputSize = new Vector2Int(shape.Get(2), shape.Get(3));
                _worker = new Worker(model, backend);
                ModelLoaded = true;
                Debug.Log($"[Vision] model loaded: input {_inputSize.x}x{_inputSize.y}, backend {backend}, {_labels.Length} classes.");
            }
            catch (Exception e)
            {
                Fail("model failed to load: " + e.Message);
            }
        }

        private void Start()
        {
            // Exactly one retained loop. SetActive(false) stops component-owned coroutines, so
            // neither this loop nor an in-flight inference is hosted on the scanner GameObject.
            if (_automaticOperation == null)
                _automaticOperation = VisionInferenceLifetime.Run(AutomaticLoop(), OperationFailed);
        }

        private IEnumerator AutomaticLoop()
        {
            if (_destroyRequested) yield break;
            LoadModel();

            while (!ModelLoaded)
            {
                if (_destroyRequested) yield break;
                if (!string.IsNullOrEmpty(LastError)) yield break;   // a load failure is terminal, don't spin
                yield return null;
            }

            while (!_destroyRequested)
            {
                // An externally submitted recorded/live call owns its output polling until its
                // retained body finishes. Do not issue recovery readbacks against that same work.
                if (_activeOperation != null && !_activeOperation.IsComplete)
                {
                    yield return null;
                    continue;
                }
                if (_backendScheduled)
                {
                    yield return DrainBackend();
                    ReleaseDrainedSnapshot();
                    InferenceRunning = false;
                    if (_destroyRequested) yield break;
                }
                var minInterval = maxInferencesPerSecond > 0f ? 1f / maxInferencesPerSecond : 0f;
                // The independent pump survives both component and parent/GameObject disable.
                // No new operation starts until any interrupted backend operation has drained.
                if (Paused || _applicationPaused || !isActiveAndEnabled || _camera == null || !_camera.IsReady || !_camera.HasFreshFrame ||
                    Time.time - _lastInferenceStartedAt < minInterval)
                {
                    yield return null;
                    continue;
                }
                yield return RunInference();
            }
        }

        private IEnumerator RunInference()
        {
            var camera = _camera;
            var startedGeneration = _publicationGeneration; // Latch BEFORE readback, not after a pause/resume.
            if (!camera.TryBeginSnapshot())
            {
                ReportCaptureError(camera.LastCaptureError);
                yield return null;
                yield break;
            }
            while (camera != null)
            {
                var status = camera.PollSnapshot(Time.realtimeSinceStartupAsDouble);
                ReportCaptureError(camera.LastCaptureError);
                if (status != CameraSnapshotStatus.Pending && status != CameraSnapshotStatus.Draining) break;
                yield return null; // Expired/invalidated requests drain; never reuse their slot early.
            }
            if (_destroyRequested || camera == null) yield break;
            if (Paused || _applicationPaused || !isActiveAndEnabled || startedGeneration != _publicationGeneration)
            {
                camera.InvalidateSnapshots();
                yield break;
            }
            if (!camera.TryTakeSnapshot(out var snapshot)) yield break;
            try
            {
                yield return DetectFrame(snapshot.Texture, snapshot.Stamp.Pose,
                    new DetectionFrameTiming(snapshot.Stamp.AcquiredAtRealtimeSeconds), startedGeneration, snapshot.Stamp.Generation);
            }
            finally
            {
                if (_backendScheduled) _snapshotAwaitingDrain = camera;
                else camera.ReleaseSnapshot();
            }
        }

        private void ReportCaptureError(string reason)
        {
            if (!string.IsNullOrEmpty(reason) && LastError != reason) Fail(reason);
        }

        /// <summary>
        /// One recorded/legacy image inference. Uses the same preprocessing, model and decoder as
        /// live inference, but has no sensor-freshness claim and is not subject to the live age limit.
        /// </summary>
        public IEnumerator DetectOnce(Texture texture, Pose cameraPose)
            => DetectFrame(texture, cameraPose, default, _publicationGeneration);

        /// <summary>Live inference with explicit monotonic application-acquisition time, not a fabricated sensor timestamp.</summary>
        public IEnumerator DetectLiveFrame(Texture texture, Pose cameraPose, double acquiredAtRealtimeSeconds)
            => DetectFrame(texture, cameraPose, new DetectionFrameTiming(acquiredAtRealtimeSeconds), _publicationGeneration);

        private IEnumerator DetectFrame(Texture texture, Pose cameraPose, DetectionFrameTiming frameTiming, int startedGeneration,
            int? capturedStreamGeneration = null)
        {
            if (_destroyRequested || _resourcesDisposed || _backendScheduled ||
                (_activeOperation != null && !_activeOperation.IsComplete)) yield break;
            // The caller is only a waiter. A preview/Editor coroutine stopping or its GameObject
            // being destroyed cannot abandon the submitted operation and its output tensors.
            var operation = VisionInferenceLifetime.Run(DetectFrameBody(texture, cameraPose, frameTiming,
                startedGeneration, capturedStreamGeneration), OperationFailed);
            _activeOperation = operation;
            while (!operation.IsComplete) yield return null;
            if (_activeOperation == operation) _activeOperation = null;
        }

        private IEnumerator DetectFrameBody(Texture texture, Pose cameraPose, DetectionFrameTiming frameTiming, int startedGeneration,
            int? capturedStreamGeneration)
        {
            if (_destroyRequested || !ModelLoaded || texture == null || InferenceRunning || Paused || _applicationPaused) yield break;

            _lastInferenceStartedAt = Time.time;
            var startedAt = Time.realtimeSinceStartup;
            InferenceRunning = true;

            // Schedule. Anything thrown here must not escape: an exception out of a coroutine kills
            // the loop permanently, and the app would keep running with passthrough and no
            // detections, saying nothing about why.
            if (!TrySchedule(texture)) { InferenceRunning = false; yield break; }

            Tensor<float> boxes = null;
            Tensor<int> classIds = null;
            Tensor<float> scores = null;

            var readback = ReadThree(b => boxes = b, c => classIds = c, s => scores = s);
            var timedOut = false;
            while (true)
            {
                // A readback cannot safely be cancelled. Report a stale frame once, but keep polling
                // its existing request before scheduling another frame on the same worker. No clone
                // exists yet, so abandoning a Unity Awaitable cannot leak a late native allocation.
                if (!timedOut && Time.realtimeSinceStartup - startedAt > readbackTimeoutSeconds)
                {
                    timedOut = true;
                    Fail($"inference readback stalled after {readbackTimeoutSeconds}s; waiting for it to finish before scanning resumes");
                }
                if (!TryAdvanceReadback(readback, out var waiting))
                {
                    InferenceRunning = false;
                    DisposeAll(boxes, classIds, scores);
                    yield break;
                }
                if (!waiting) break;
                yield return readback.Current;
            }

            InferenceRunning = false;
            if (timedOut)
            {
                DisposeAll(boxes, classIds, scores);
                yield break; // Never publish the old camera pose after a long stall.
            }
            LastInferenceMs = (Time.realtimeSinceStartup - startedAt) * 1000f;
            TotalInferences++;

            if (boxes == null || classIds == null || scores == null)
            {
                Fail("inference produced no output tensors");
                DisposeAll(boxes, classIds, scores);
                yield break;
            }

            var decoded = TryDecode(boxes, classIds, scores);
            DisposeAll(boxes, classIds, scores);
            if (!decoded) yield break;

            var publishedAt = Time.realtimeSinceStartupAsDouble;
            LastPublicationRejection = DetectionPublicationGate.Evaluate(frameTiming, publishedAt,
                maxLiveResultAgeSeconds, startedGeneration, _publicationGeneration, _destroyRequested || Paused || _applicationPaused);
            if (capturedStreamGeneration.HasValue && (_camera == null || !_camera.IsReady ||
                _camera.CaptureGeneration != capturedStreamGeneration.Value))
                LastPublicationRejection = DetectionPublicationRejection.PausedOrSuperseded;
            if (LastPublicationRejection != DetectionPublicationRejection.None)
            {
                DiscardedResults++;
                yield break;
            }

            _publicationCadence.Record(publishedAt);
            OnDetectionFrame?.Invoke(_detections, cameraPose, _sourceSize, frameTiming);
            OnDetections?.Invoke(_detections, cameraPose, _sourceSize);
        }

        /// <summary>
        /// Allocation-free diagnostic clock with injected monotonic timestamps for deterministic tests.
        /// Smooths the interval between successful publications, which includes the intentional rate cap,
        /// camera waits, preprocessing and inference. Processing latency remains a separate measurement.
        /// A first sample, pause/reset, invalid clock, or a gap above two seconds has no current rate yet.
        /// </summary>
        public sealed class PublicationCadence
        {
            public const double ResetGapSeconds = 2d;
            private double _lastPublishedAt = double.NaN;
            private double _meanInterval;

            public void Record(double now)
            {
                if (!Valid(now)) { Reset(); return; }
                var interval = now - _lastPublishedAt;
                if (!double.IsNaN(_lastPublishedAt) && interval > 0d && interval <= ResetGapSeconds)
                    _meanInterval = _meanInterval > 0d ? _meanInterval + (interval - _meanInterval) * .2d : interval;
                else
                    _meanInterval = 0d;
                _lastPublishedAt = now;
            }

            public float Read(double now)
            {
                if (!Valid(now) || double.IsNaN(_lastPublishedAt) || now < _lastPublishedAt || now - _lastPublishedAt > ResetGapSeconds)
                {
                    Reset();
                    return 0f;
                }
                return _meanInterval > 0d ? (float)(1d / _meanInterval) : 0f;
            }

            public void Reset()
            {
                _lastPublishedAt = double.NaN;
                _meanInterval = 0d;
            }

            private static bool Valid(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0d;
        }

        /// <summary>Decode + NMS. Isolated so the coroutine never has a yield inside a try/catch.</summary>
        private bool TryDecode(Tensor<float> boxes, Tensor<int> classIds, Tensor<float> scores)
        {
            try
            {
                LastRawDetections = scores.shape.length;
                if (collectRawClassSummaries) SummarizeRawClasses(classIds, scores);
                if (!_loggedRawBox && scores.shape.length > 0)
                {
                    _loggedRawBox = true;                                    // one line, so the log settles the layout on a real device
                    Debug.Log($"[Vision] first raw box [{boxes[0, 0]:0.0}, {boxes[0, 1]:0.0}, {boxes[0, 2]:0.0}, {boxes[0, 3]:0.0}] " +
                              $"read as {(cornerBoxes ? "corners (x1,y1,x2,y2)" : "centre+size (cx,cy,w,h)")}");
                }
                NonMaxSuppression(_nmsResults, boxes, classIds, scores, iouThreshold, scoreThreshold, cornerBoxes);

                _detections.Clear();
                foreach (var (classId, box, score) in _nmsResults)
                {
                    var rect = _letterbox.Layout.ToSourceRect(new Rect(box.x, box.y, box.z - box.x, box.w - box.y));
                    // Ignore padding-only detections wholly outside the camera image. Partially
                    // visible objects retain their raw extent rather than being silently clamped.
                    if (rect.xMax <= 0f || rect.yMax <= 0f || rect.xMin >= _sourceSize.x || rect.yMin >= _sourceSize.y) continue;
                    _detections.Add(new DetectedObject
                    {
                        classId = classId,
                        className = ClassName(classId),
                        confidence = score,
                        boundingBox = rect,
                        centerPixel = rect.center,
                        inputSize = _sourceSize,
                    });
                }
                LastAcceptedDetections = _detections.Count;
                LastError = "";
                return true;
            }
            catch (Exception e)
            {
                Fail("decoding detections failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Preprocess + schedule. Returns false and records why if anything throws.</summary>
        private bool TrySchedule(Texture texture)
        {
            try
            {
                if (_input == null) _input = new Tensor<float>(new TensorShape(1, 3, _inputSize.x, _inputSize.y));
                _sourceSize = new Vector2Int(texture.width, texture.height);
                var prepared = _letterbox.Prepare(texture, _inputSize);
                TextureConverter.ToTensor(prepared, _input);
                _drainRequested = false;
                _drainBoxes = _drainClasses = _drainScores = null;
                // Set before Schedule: even a partially failed schedule must not lead to early
                // worker/input disposal or reuse while its submitted work may still be running.
                _backendScheduled = true;
                _worker.Schedule(_input);
                return true;
            }
            catch (Exception e)
            {
                // A Schedule exception can leave only prior-submission outputs addressable.
                // Those outputs cannot prove the partially submitted work has completed.
                if (_backendScheduled) _backendCompletionUnknown = true;
                Fail("scheduling inference failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Request first, poll without allocating, then own each CPU clone immediately.</summary>
        private IEnumerator ReadThree(Action<Tensor<float>> gotBoxes, Action<Tensor<int>> gotClasses,
            Action<Tensor<float>> gotScores)
        {
            var boxes = _worker.PeekOutput(0) as Tensor<float>
                ?? throw new InvalidOperationException("model output 0 must contain float boxes");
            var classes = _worker.PeekOutput(1) as Tensor<int>
                ?? throw new InvalidOperationException("model output 1 must contain integer class IDs");
            var scores = _worker.PeekOutput(2) as Tensor<float>
                ?? throw new InvalidOperationException("model output 2 must contain float scores");
            boxes.ReadbackRequest();
            classes.ReadbackRequest();
            scores.ReadbackRequest();
            while (!ReadbackComplete(boxes) || !ReadbackComplete(classes) || !ReadbackComplete(scores))
                yield return null;

            _backendScheduled = false; // All worker-owned outputs are now complete, before any clone can throw.

            // All backend work is complete before these CPU copies. If a later copy throws, the
            // caller already owns and can dispose every earlier copy rather than leaking them.
            gotBoxes(boxes.ReadbackAndClone());
            gotClasses(classes.ReadbackAndClone());
            gotScores(scores.ReadbackAndClone());
        }

        private static bool ReadbackComplete(Tensor tensor) => tensor.count == 0 || tensor.IsReadbackRequestDone();

        private bool TryAdvanceReadback(IEnumerator readback, out bool waiting)
        {
            try { waiting = readback.MoveNext(); return true; }
            catch (Exception e)
            {
                waiting = false;
                Fail("reading inference outputs failed: " + e.Message);
                return false;
            }
        }

        private static void DisposeAll(Tensor<float> a, Tensor<int> b, Tensor<float> c)
        {
            a?.Dispose(); b?.Dispose(); c?.Dispose();
        }

        private string ClassName(int id) => _labels != null && id >= 0 && id < _labels.Length ? _labels[id] : $"class{id}";

        /// <summary>
        /// The sample's COCO label file uses several concatenated legacy names. Normalize once when
        /// loading, so labels read naturally and class-specific sizing uses the same vocabulary.
        /// Class IDs stay unchanged: this does not add categories beyond the model's 80 COCO classes.
        /// </summary>
        public static string NormalizeClassName(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "object";
            return label.Trim() switch
            {
                "diningtable" => "dining table",
                "tvmonitor" => "TV / monitor",
                "pottedplant" => "potted plant",
                _ => label.Trim(),
            };
        }

        private void Fail(string reason)
        {
            LastError = reason;
            Debug.LogError("[Vision] " + reason);
        }

        private void SummarizeRawClasses(Tensor<int> classIds, Tensor<float> scores)
        {
            LastRawClassThreshold = scoreThreshold;
            for (var i = 0; i < _rawClassSummaries.Length; i++)
                _rawClassSummaries[i] = new RawClassSummary(i, ClassName(i), 0, 0, 0f);

            var scoreArray = scores.AsReadOnlyNativeArray();
            for (var i = 0; i < scoreArray.Length; i++)
            {
                var classId = classIds[i];
                if (classId < 0 || classId >= _rawClassSummaries.Length) continue;
                var previous = _rawClassSummaries[classId];
                var score = scoreArray[i];
                _rawClassSummaries[classId] = new RawClassSummary(classId, previous.className,
                    previous.candidateCount + 1, previous.aboveThreshold + (score >= scoreThreshold ? 1 : 0),
                    Mathf.Max(previous.maxScore, score));
            }
        }

        /// <summary>
        /// Score filter, sort, then suppress duplicate boxes of the SAME class. Meta's sample uses
        /// class-agnostic suppression; that would erase a table whenever an object on it overlaps
        /// enough (for example, a large pizza), although both are independently recognized objects.
        /// </summary>
        private static void NonMaxSuppression(List<(int classId, Vector4 box, float score)> results, Tensor<float> boxes,
            Tensor<int> classIds, Tensor<float> scores, float iouThreshold, float scoreThreshold, bool cornerBoxes)
        {
            results.Clear();
            NativeArray<float>.ReadOnly scoreArray = scores.AsReadOnlyNativeArray();

            var candidates = new List<int>();
            for (var i = 0; i < scoreArray.Length; i++)
                if (scoreArray[i] >= scoreThreshold) candidates.Add(i);
            if (candidates.Count == 0) return;

            candidates.Sort((a, b) => scoreArray[b].CompareTo(scoreArray[a]));

            var suppressed = new bool[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
            {
                if (suppressed[i]) continue;
                var idx = candidates[i];
                results.Add((classIds[idx], Box(idx), scoreArray[idx]));

                for (var j = i + 1; j < candidates.Count; j++)
                {
                    if (suppressed[j]) continue;
                    var other = candidates[j];
                    if (ShouldSuppressDetection(classIds[idx], Box(idx), classIds[other], Box(other), iouThreshold))
                        suppressed[j] = true;
                }
            }

            Vector4 Box(int i) => DecodeModelBox(
                new Vector4(boxes[i, 0], boxes[i, 1], boxes[i, 2], boxes[i, 3]), cornerBoxes);
        }

        /// <summary>
        /// Meta's SentisModelEditorConverter bakes a centres-to-corners MatMul into the model, so its
        /// first output is already x1,y1,x2,y2. Our bundled FP32 export recreates that same output
        /// graph from the pinned original ONNX without quantizing its weights; it is not the old
        /// byte-identical quantized sample binary. Model conversion provenance is recorded separately.
        /// Converting it again changes the centre used by camera/depth rays, not just the drawing.
        /// The explicit false option is retained for replacement models with raw centre+size output.
        /// </summary>
        public static Vector4 DecodeModelBox(Vector4 raw, bool corners = true)
            => corners ? raw : new Vector4(raw.x - raw.z * 0.5f, raw.y - raw.w * 0.5f,
                raw.x + raw.z * 0.5f, raw.y + raw.w * 0.5f);

        /// <summary>Different object classes may overlap without being duplicate detections.</summary>
        public static bool ShouldSuppressDetection(int keptClassId, Vector4 keptBox,
            int candidateClassId, Vector4 candidateBox, float iouThreshold)
            => keptClassId == candidateClassId && IoU(keptBox, candidateBox) > iouThreshold;

        private static float IoU(Vector4 a, Vector4 b)
        {
            var x1 = Mathf.Max(a.x, b.x);
            var y1 = Mathf.Max(a.y, b.y);
            var x2 = Mathf.Min(a.z, b.z);
            var y2 = Mathf.Min(a.w, b.w);
            var overlap = Mathf.Max(0f, x2 - x1) * Mathf.Max(0f, y2 - y1);
            var areaA = Mathf.Max(0f, a.z - a.x) * Mathf.Max(0f, a.w - a.y);
            var areaB = Mathf.Max(0f, b.z - b.x) * Mathf.Max(0f, b.w - b.y);
            var union = areaA + areaB - overlap;
            return union <= 0f ? 0f : overlap / union;
        }

        private void OnDisable()
        {
            unchecked { ++_publicationGeneration; }
            _publicationCadence.Reset();
            _camera?.InvalidateSnapshots();
        }

        private void OnApplicationPause(bool paused)
        {
            _applicationPaused = paused;
            unchecked { ++_publicationGeneration; }
            _publicationCadence.Reset();
            _camera?.InvalidateSnapshots();
        }

        private void OnDestroy()
        {
            _destroyRequested = true;
            unchecked { ++_publicationGeneration; }
            _camera?.InvalidateSnapshots();
            // Engine/process shutdown has no future player updates. Do not invent successful
            // drainage or force a blocking wait; native process teardown handles pending work.
            if ((_automaticOperation == null || _automaticOperation.IsComplete) &&
                (_activeOperation == null || _activeOperation.IsComplete) && !_backendScheduled)
                DisposeResources();
            else if (_applicationQuitting && !VisionInferenceLifetime.HasEditorDriver) return;
            else if (_retirementOperation == null)
                _retirementOperation = VisionInferenceLifetime.Run(RetireResources(), OperationFailed);
        }

        private void OnApplicationQuit() => _applicationQuitting = true;

        private IEnumerator RetireResources()
        {
            // Check only managed flags/handles after owner destruction: do not read this.enabled,
            // transform, gameObject or other Unity destroyed-object properties here.
            while ((_automaticOperation != null && !_automaticOperation.IsComplete) ||
                (_activeOperation != null && !_activeOperation.IsComplete)) yield return null;
            if (_backendScheduled) yield return DrainBackend();
            ReleaseDrainedSnapshot();
            DisposeResources();
        }

        private IEnumerator DrainBackend()
        {
            while (_backendScheduled)
            {
                if (TryDrainBackend()) break;
                yield return null;
            }
        }

        private bool TryDrainBackend()
        {
            if (_backendCompletionUnknown)
            {
                ReportCaptureError("Inference backend quarantined after partial scheduling failure: current-submission completion is unknown. Worker/input/lease retained until process exit; restart the app to recover.");
                return false;
            }
            try
            {
                if (!_drainRequested)
                {
                    _drainBoxes = _worker.PeekOutput(0);
                    _drainClasses = _worker.PeekOutput(1);
                    _drainScores = _worker.PeekOutput(2);
                    if (_drainBoxes == null || _drainClasses == null || _drainScores == null)
                        throw new InvalidOperationException("Cannot establish completion of all scheduled worker outputs.");
                    _drainBoxes.ReadbackRequest();
                    _drainClasses.ReadbackRequest();
                    _drainScores.ReadbackRequest();
                    _drainRequested = true;
                }
                if (!ReadbackComplete(_drainBoxes) || !ReadbackComplete(_drainClasses) || !ReadbackComplete(_drainScores)) return false;
                _backendScheduled = false;
                return true;
            }
            catch (Exception error)
            {
                ReportCaptureError("Inference retirement is waiting for safe backend completion: " + error.Message);
                return false; // Never reinterpret an unknown completion state as safe reuse/disposal.
            }
        }

        private void ReleaseDrainedSnapshot()
        {
            // ?. intentionally uses the managed reference even if its VisionCamera was destroyed;
            // ReleaseSnapshot only touches the owned snapshot helper, not native component state.
            _snapshotAwaitingDrain?.ReleaseSnapshot();
            _snapshotAwaitingDrain = null;
        }

        private void OperationFailed(Exception error)
        {
            ReportCaptureError("Retained vision operation failed: " + error.Message);
            // A fault is not evidence of backend completion; DrainBackend owns that decision.
            if (!_backendScheduled) InferenceRunning = false;
        }

        private void DisposeResources()
        {
            if (_resourcesDisposed) return;
            if (_backendScheduled) throw new InvalidOperationException("Cannot dispose an inference backend before drainage.");
            _resourcesDisposed = true;
            _worker?.Dispose();
            _input?.Dispose();
            _letterbox.Dispose();
            _camera?.ReleaseSnapshot();
            _worker = null;
            _input = null;
            InferenceRunning = false;
        }
    }
}
