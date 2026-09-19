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
    /// (SentisInferenceRunManager). Same model, same package, same async shape — the parts kept
    /// verbatim are the ones that are easy to get subtly wrong: the tensor layout, the readback
    /// pattern, and the NMS.
    ///
    /// Two deliberate departures from the sample:
    ///  - its RunInference() opens with a local DllImport of ovrp_GetNodePoseStateAtTime, an
    ///    undocumented P/Invoke into OVRPlugin, purely to decide whether the camera pose is
    ///    trustworthy. We skip frames using the camera's own IsUpdatedThisFrame instead, which is
    ///    public API and tells us the same thing for our purposes.
    ///  - the sample drives a world-space uGUI canvas; we raise an event and let the rest of the
    ///    pipeline decide what to do with it.
    ///
    /// Never blocks the render thread: Schedule() queues the layers and every readback is polled
    /// across frames with `yield return null` until it completes.
    /// </summary>
    public class YoloDetector : MonoBehaviour
    {
        [Header("Model (Meta's yolov9sentis.sentis + its COCO labels)")]
        public ModelAsset modelAsset;
        public TextAsset labelsAsset;

        [Tooltip("CPU is what Meta ships. Their maintainers found GPUPixel does not support the quantized model and GPU was slower.")]
        public BackendType backend = BackendType.CPU;

        [Range(0f, 1f)] public float scoreThreshold = 0.35f;
        [Range(0f, 1f)] public float iouThreshold = 0.5f;

        [Tooltip("Abandon a frame whose readback never lands, rather than spinning the frame loop forever.")]
        public float readbackTimeoutSeconds = 8f;

        [Tooltip("Upper bound on inference rate. The model takes as long as it takes; this only stops us queueing faster than that. A few a second keep the labels live without cooking an XR2.")]
        public float maxInferencesPerSecond = 3f;

        [Tooltip("Only for a model whose head already gives corners (x1,y1,x2,y2). YOLO gives centre+size.")]
        public bool cornerBoxes;

        private bool _loggedRawBox;

        /// <summary>Detections, plus the camera pose and input size they were computed against.</summary>
        public event Action<List<DetectedObject>, Pose, Vector2> OnDetections;

        // ---- diagnostics, so a dead model can never masquerade as a working one ----
        public bool ModelLoaded { get; private set; }
        public bool InferenceRunning { get; private set; }
        public float LastInferenceMs { get; private set; }
        public float InferencesPerSecond { get; private set; }
        public int LastRawDetections { get; private set; }
        public int LastAcceptedDetections { get; private set; }
        public int TotalInferences { get; private set; }
        public string LastError { get; private set; } = "";

        private Worker _worker;
        private Tensor<float> _input;
        private Vector2Int _inputSize;
        private string[] _labels;
        private VisionCamera _camera;
        private readonly List<(int classId, Vector4 box, float score)> _nmsResults = new();
        private readonly List<DetectedObject> _detections = new();
        private float _lastInferenceStartedAt = -999f;
        private float _emaFps;

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
            for (var i = 0; i < _labels.Length; i++) _labels[i] = _labels[i].Trim();

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

        private IEnumerator Start()
        {
            LoadModel();

            while (!ModelLoaded)
            {
                if (!string.IsNullOrEmpty(LastError)) yield break;   // a load failure is terminal, don't spin
                yield return null;
            }

            while (enabled)
            {
                var minInterval = maxInferencesPerSecond > 0f ? 1f / maxInferencesPerSecond : 0f;
                if (_camera == null || !_camera.IsReady || !_camera.HasFreshFrame ||
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
            var texture = _camera.GetTexture();
            if (texture == null) { yield return null; yield break; }

            // Cache the pose BEFORE inference: it describes the image we are about to run on, and by
            // the time results land the head has moved. Everything downstream must use this pose.
            var cameraPose = _camera.GetCameraPose();
            yield return DetectOnce(texture, cameraPose);
        }

        /// <summary>
        /// One inference on one image. RunInference feeds this the live camera texture; tests feed
        /// it a fixture, so the path under test is the same one the headset runs.
        /// </summary>
        public IEnumerator DetectOnce(Texture texture, Pose cameraPose)
        {
            if (!ModelLoaded || texture == null) yield break;

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

            var readback = ReadThree(r => { boxes = r.Item1; classIds = r.Item2; scores = r.Item3; });
            while (readback.MoveNext())
            {
                // Bounded: an unbounded `while (!IsCompleted) yield return null` spins the frame loop
                // at 100% forever if a readback never lands, which is a hang, not a slow frame.
                if (Time.realtimeSinceStartup - startedAt > readbackTimeoutSeconds)
                {
                    Fail($"inference readback did not complete within {readbackTimeoutSeconds}s; skipping frame");
                    InferenceRunning = false;
                    DisposeAll(boxes, classIds, scores);
                    yield break;
                }
                yield return readback.Current;
            }

            InferenceRunning = false;
            LastInferenceMs = (Time.realtimeSinceStartup - startedAt) * 1000f;
            TotalInferences++;
            var instant = LastInferenceMs > 0f ? 1000f / LastInferenceMs : 0f;
            _emaFps = _emaFps <= 0f ? instant : Mathf.Lerp(_emaFps, instant, 0.2f);
            InferencesPerSecond = _emaFps;

            if (boxes == null || classIds == null || scores == null)
            {
                Fail("inference produced no output tensors");
                DisposeAll(boxes, classIds, scores);
                yield break;
            }

            var decoded = TryDecode(boxes, classIds, scores);
            DisposeAll(boxes, classIds, scores);
            if (!decoded) yield break;

            OnDetections?.Invoke(_detections, cameraPose, _inputSize);
        }

        /// <summary>Decode + NMS. Isolated so the coroutine never has a yield inside a try/catch.</summary>
        private bool TryDecode(Tensor<float> boxes, Tensor<int> classIds, Tensor<float> scores)
        {
            try
            {
                LastRawDetections = scores.shape.length;
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
                    var rect = new Rect(box.x, box.y, box.z - box.x, box.w - box.y);
                    _detections.Add(new DetectedObject
                    {
                        classId = classId,
                        className = ClassName(classId),
                        confidence = score,
                        boundingBox = rect,
                        centerPixel = rect.center,
                        inputSize = _inputSize,
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
                var transform = new TextureTransform().SetDimensions(_inputSize.x, _inputSize.y, 3);   // the tensor's size: the camera frame is not square, the model's input is
                TextureConverter.ToTensor(texture, _input, transform);
                _worker.Schedule(_input);
                return true;
            }
            catch (Exception e)
            {
                Fail("scheduling inference failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Poll the three output readbacks across frames, handing them back when all land.</summary>
        private IEnumerator ReadThree(Action<(Tensor<float>, Tensor<int>, Tensor<float>)> done)
        {
            var boxesAwaiter = (_worker.PeekOutput(0) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
            while (!boxesAwaiter.IsCompleted) yield return null;

            var classesAwaiter = (_worker.PeekOutput(1) as Tensor<int>).ReadbackAndCloneAsync().GetAwaiter();
            while (!classesAwaiter.IsCompleted) yield return null;

            var scoresAwaiter = (_worker.PeekOutput(2) as Tensor<float>).ReadbackAndCloneAsync().GetAwaiter();
            while (!scoresAwaiter.IsCompleted) yield return null;

            done((boxesAwaiter.GetResult(), classesAwaiter.GetResult(), scoresAwaiter.GetResult()));
        }

        private static void DisposeAll(Tensor<float> a, Tensor<int> b, Tensor<float> c)
        {
            a?.Dispose(); b?.Dispose(); c?.Dispose();
        }

        private string ClassName(int id) => _labels != null && id >= 0 && id < _labels.Length ? _labels[id] : $"class{id}";

        private void Fail(string reason)
        {
            LastError = reason;
            Debug.LogError("[Vision] " + reason);
        }

        /// <summary>Ported from Meta's sample: score filter, sort, suppress by IoU regardless of class.</summary>
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
                    if (IoU(Box(idx), Box(candidates[j])) > iouThreshold) suppressed[j] = true;
                }
            }

            // The head gives each box as centre-width-height — Ultralytics' own layout, and what Meta's sample reads
            // out of this very model. Everything downstream (IoU here, the Rect below) is corners, so it is converted
            // once, here. Read as corners, a box 100 px wide at x = 320 becomes 220 px WIDE OF ZERO: the rectangle
            // inverts, its centre lands nowhere near the object, and IoU stops suppressing duplicates.
            Vector4 Box(int i)
            {
                float a = boxes[i, 0], b = boxes[i, 1], c = boxes[i, 2], d = boxes[i, 3];
                return cornerBoxes ? new Vector4(a, b, c, d) : new Vector4(a - c * 0.5f, b - d * 0.5f, a + c * 0.5f, b + d * 0.5f);
            }
        }

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

        private void OnDestroy()
        {
            StopAllCoroutines();                                             // a readback still in flight must not land on a disposed worker
            _worker?.Dispose();
            _input?.Dispose();
        }
    }
}
