using System.Collections;
using System.Collections.Generic;
using CutOnce.Core.Vision;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// The whole scanner, wired and started for you.
    ///
    /// Put the headset on and it runs: permissions are requested, the camera starts, the model
    /// loads, and detections begin. No controller button, no menu, no "start scan".
    ///
    /// Chain: camera texture -> YOLO -> boxes -> ray through the cached pose -> environment depth
    /// -> world point -> tracker -> blue highlight + the model's own class name.
    /// </summary>
    public class RoomScanner : MonoBehaviour
    {
        [Header("Assets (assigned by RoomScannerBootstrap when auto-installed)")]
        public Unity.InferenceEngine.ModelAsset modelAsset;
        public TextAsset labelsAsset;

        [Header("Behaviour")]
        [Tooltip("Ignore detections below this. The model's own score threshold is separate and lower.")]
        [Range(0f, 1f)] public float minConfidence = 0.4f;
        public bool logDetections = true;
        public float logInterval = 1f;

        [Header("Measuring")]
        [Tooltip("Boxes measured per second, at most one per frame. Each costs a few hundred raycasts, so this is the frame-time dial; the smoother holds every box steady in between.")]
        [Range(1f, 30f)] public float measurementsPerSecond = 8f;

        [Tooltip("Show objects whose box has not been measured yet, using the old size-from-one-distance estimate. Off on the headset: that estimate is what drew metre-wide boxes around people.")]
        public bool showUnmeasured = false;

        public VisionCamera Camera { get; private set; }
        public YoloDetector Detector { get; private set; }
        public Object3DLocator Locator { get; private set; }
        public TrackedObjectManager Tracker { get; private set; }
        public ObjectVisualizer Visualizer { get; private set; }

        /// <summary>Live health line — camera, model and depth, so a dead pipeline cannot look alive.</summary>
        public string Status
        {
            get
            {
                if (Detector != null && !string.IsNullOrEmpty(Detector.LastError)) return "detector error: " + Detector.LastError;
                if (Camera == null || !Camera.IsReady) return _status;
                if (Detector == null || !Detector.ModelLoaded) return "camera up, model not loaded yet";
                if (!Locator.IsSupported) return "scanning (no depth — objects sit on the room's surfaces, not on themselves)";
                return $"scanning — {Detector.InferencesPerSecond:0.0}/s, {Tracker.VisibleCount} objects";
            }
        }

        private string _status = "starting";

        private readonly HashSet<int> _observed = new();
        private readonly List<TrackedObject> _removed = new();
        private float _nextLogAt;

        private void Awake()
        {
            // Self-heal if someone drops this component into a scene by hand without wiring assets.
            if (modelAsset == null) modelAsset = Resources.Load<Unity.InferenceEngine.ModelAsset>(RoomScannerBootstrap.ModelResource);
            if (labelsAsset == null) labelsAsset = Resources.Load<TextAsset>(RoomScannerBootstrap.LabelsResource);

            Camera = gameObject.AddComponent<VisionCamera>();

            Detector = gameObject.AddComponent<YoloDetector>();
            Detector.modelAsset = modelAsset;
            Detector.labelsAsset = labelsAsset;

            Locator = gameObject.AddComponent<Object3DLocator>();
            Tracker = gameObject.AddComponent<TrackedObjectManager>();
            Visualizer = gameObject.AddComponent<ObjectVisualizer>();

            Detector.OnDetections += HandleDetections;
        }

        // ── the headset coming and going ───────────────────────────────────────────────────────────────────────────
        // A tracked object is a world-space position, and world space only means the same thing while the tracking
        // origin stays put. Take the headset off, walk somewhere, put it back on, and the origin may have been
        // recentred under everything that was tracked — so the boxes hang in the air where the objects USED to be
        // relative to an origin that no longer exists.
        //
        // The keep-alive cannot catch this on its own: Time.time does not advance while the app is paused, so after
        // a five-minute break every object still looks like it was seen 40 ms ago and Prune keeps all of it.
        //
        // So: when the headset is taken off, or tracking is re-acquired, forget the room. Everything is re-detected
        // and re-measured within a second, which is cheaper and more honest than trying to correct stale positions.
        // (The BUILD does not need this — it hangs off an OVRSpatialAnchor, which the system re-localises itself.)

        private void OnEnable()
        {
            OVRManager.HMDUnmounted += ForgetTheRoom;
            OVRManager.TrackingAcquired += ForgetTheRoom;
        }

        private void OnDisable()
        {
            OVRManager.HMDUnmounted -= ForgetTheRoom;
            OVRManager.TrackingAcquired -= ForgetTheRoom;
        }

        /// <summary>The Editor and the simulator never raise the HMD events; a pause is the same story there.</summary>
        private void OnApplicationPause(bool paused)
        {
            if (!paused) ForgetTheRoom();
        }

        private void ForgetTheRoom()
        {
            if (Tracker == null) return;
            var dropped = Tracker.Forget(_removed);
            if (Visualizer != null) foreach (var o in dropped) Visualizer.Release(o);
            if (dropped.Count > 0) Debug.Log($"[Vision] the headset moved: forgetting {dropped.Count} tracked object(s) and looking again.");
        }

        private IEnumerator Start()
        {
            _status = "waiting for camera";
            var waitedFrom = Time.time;
            while (!Camera.IsReady)
            {
                if (Time.time - waitedFrom > 20f)
                {
                    _status = "camera never started — check HEADSET_CAMERA permission was granted";
                    Debug.LogError("[Vision] " + _status);
                    yield break;
                }
                yield return null;
            }

            _status = "scanning";
            Debug.Log($"[Vision] CAMERA READY {Camera.Resolution.x}x{Camera.Resolution.y}; depth={(Locator.IsSupported ? "supported" : "UNSUPPORTED — positions will fail")}.");
        }

        private void HandleDetections(List<DetectedObject> detections, Pose cameraPose, Vector2 inputSize)
        {
            _observed.Clear();
            var located = 0;
            var ignored = 0;
            var shouldLog = logDetections && Time.time >= _nextLogAt;

            foreach (var d in detections)
            {
                if (d.confidence < minConfidence) continue;

                // People and furniture are never boxed. Dropped HERE, before any depth is touched, so a person in
                // the room costs nothing at all — no raycasts, no tracking, no hologram. A box around a judge reads
                // as surveillance, and a sofa's box swallows the view.
                if (SizePriors.Ignored(d.className)) { ignored++; continue; }

                if (!Locator.TryLocate(d, cameraPose, out var world, out var worldSize)) continue;

                located++;
                var tracked = Tracker.Observe(d, world, worldSize);
                _observed.Add(tracked.id);

                // Keep the newest sighting so the box can be measured later, at its own pace, instead of paying for
                // a few hundred raycasts inside the frame that ran inference.
                tracked.lastBox = d.boundingBox;
                tracked.lastInputSize = d.inputSize;
                tracked.lastPose = cameraPose;
                tracked.hasLastBox = true;

                if (shouldLog) Debug.Log($"Detected: {d.className} {d.confidence:0.00}  @ {world}");
            }

            if (shouldLog)
            {
                _nextLogAt = Time.time + logInterval;
                var measured = 0;
                foreach (var o in Tracker.Objects) if (o.hasMeasuredBox) measured++;
                Debug.Log($"[Vision] {Detector.LastRawDetections} raw -> {detections.Count} after NMS -> " +
                          $"{located} located, {ignored} never-boxed | tracked {Tracker.Objects.Count} " +
                          $"({Tracker.VisibleCount} visible, {measured} measured) | " +
                          $"{Detector.LastInferenceMs:0} ms, {Detector.InferencesPerSecond:0.0}/s");
            }

            Tracker.EndFrame(_observed);
        }

        /// <summary>
        /// Off while build mode has the room: its own scan names and measures the objects, and two sets of names over
        /// one table read as noise. Stopping the detector also gives the scan, the designs and the fly-in the frame
        /// time and the headroom (an XR2 throttles when it gets hot). Everything tracked is kept, so coming back is free.
        /// </summary>
        public bool Paused
        {
            get => _paused;
            set
            {
                if (_paused == value) return;
                _paused = value;
                if (Detector != null) Detector.Paused = value;
                if (value && Visualizer != null && Tracker != null) foreach (var o in Tracker.Objects) Visualizer.Hide(o);
            }
        }

        private bool _paused;
        private UnityEngine.Camera _eye;
        private TrackedObject _focused;

        /// <summary>
        /// One box measured per frame at most, and only every so often: each costs a few hundred raycasts, and a
        /// dozen of them in the frame that just ran inference is a dropped frame you can feel. Round-robin over the
        /// tracked set, oldest measurement first, so everything in the room converges rather than whatever happens
        /// to be in front. <see cref="BoxSmoother"/> holds each box still between its turns.
        /// </summary>
        private void MeasureOne()
        {
            if (!Locator.IsSupported || Tracker.Objects.Count == 0) return;
            if (Time.time < _nextMeasureAt) return;
            _nextMeasureAt = Time.time + 1f / Mathf.Max(1f, measurementsPerSecond);

            TrackedObject oldest = null;
            foreach (var o in Tracker.Objects)
            {
                if (!o.hasLastBox) continue;
                if (oldest == null || o.lastMeasuredTime < oldest.lastMeasuredTime) oldest = o;
            }
            if (oldest == null) return;

            Locator.TryMeasure(new DetectedObject
            {
                classId = oldest.classId,
                className = oldest.className,
                confidence = oldest.confidence,
                boundingBox = oldest.lastBox,
                inputSize = oldest.lastInputSize,
            }, oldest.lastPose, SizePriors.For(oldest.className), out var fit);

            Tracker.Measure(oldest, fit);
        }

        private float _nextMeasureAt;

        private void Update()
        {
            if (_paused) return;
            foreach (var o in Tracker.Prune(_removed)) Visualizer.Release(o);
            MeasureOne();
            if (_eye == null) _eye = UnityEngine.Camera.main;
            TrackedObject focus = null;
            float best = 0f;
            if (_eye != null)
            {
                var head = _eye.transform;
                foreach (var o in Tracker.Objects)
                {
                    if (!o.visible) continue;
                    var offset = o.DisplayCentre - head.position;
                    float distance = offset.magnitude;
                    if (distance < .2f || distance > 4f) continue;
                    float facing = Vector3.Dot(head.forward, offset / distance);
                    if (facing < .93f) continue; // about 22 degrees from where you are looking
                    float score = facing + (o == _focused ? .025f : 0f); // don't flicker between neighbours
                    if (score > best) { best = score; focus = o; }
                }
            }
            _focused = focus;

            // The smart-glasses rule: every recognised object wears its subtle highlight and name;
            // the one being looked at is brighter. Capped by distance so a busy room cannot fill the
            // view (or the fill-rate budget) with see-through boxes.
            var shown = 0;
            foreach (var o in Tracker.Objects)
            {
                if (!o.visible) { Visualizer.Hide(o); continue; }
                var head = _eye != null ? _eye.transform.position : Vector3.zero;
                var near = Vector3.Distance(head, o.DisplayCentre) <= maxHighlightDistance;
                if (near && shown < maxHighlighted) { Visualizer.Show(o, o == focus, Drawable(o)); shown++; }
                else Visualizer.Hide(o);
            }

            DebugToggle();
        }

        /// <summary>
        /// Is there geometry worth drawing? Where depth exists, only a MEASURED box: the old estimate is what put
        /// metre-wide holograms around objects. Recognition still gets a label while its first fit is pending,
        /// rather than wearing a wrong box. Without depth, the Editor or Link has nothing to measure from, so the
        /// old estimate is all there is and the pipeline can still be watched end to end.
        /// </summary>
        private bool Drawable(TrackedObject o) => o.hasMeasuredBox || showUnmeasured || !Locator.IsSupported;

        [Tooltip("Recognised objects further than this keep tracking but drop their highlight.")]
        public float maxHighlightDistance = 4f;   // matches the focus range above
        [Tooltip("At most this many highlights at once — see-through surfaces are a Quest budget.")]
        public int maxHighlighted = 12;

        /// <summary>Click both thumbsticks in and hold for a second: debug labels on, again for off.</summary>
        private float _debugHeldFor;
        private void DebugToggle()
        {
            var held = OVRInput.Get(OVRInput.RawButton.LThumbstick) && OVRInput.Get(OVRInput.RawButton.RThumbstick);
            if (!held) { _debugHeldFor = 0f; return; }
            _debugHeldFor += Time.deltaTime;
            if (_debugHeldFor < 1f) return;
            _debugHeldFor = float.NegativeInfinity;   // fire once per hold
            VisionDebug.Enabled = !VisionDebug.Enabled;
            Debug.Log($"[Vision] debug labels {(VisionDebug.Enabled ? "ON" : "OFF")}.");
        }

        private void OnDestroy()
        {
            if (Detector != null) Detector.OnDetections -= HandleDetections;
        }
    }
}
