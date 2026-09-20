using System.Collections;
using System.Collections.Generic;
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
            var shouldLog = logDetections && Time.time >= _nextLogAt;

            foreach (var d in detections)
            {
                if (d.confidence < minConfidence) continue;
                if (!Locator.TryLocate(d, cameraPose, out var world)) continue;

                located++;
                var tracked = Tracker.Observe(d, world);
                _observed.Add(tracked.id);

                if (shouldLog) Debug.Log($"Detected: {d.className} {d.confidence:0.00}  @ {world}");
            }

            if (shouldLog)
            {
                _nextLogAt = Time.time + logInterval;
                Debug.Log($"[Vision] {Detector.LastRawDetections} raw -> {detections.Count} after NMS -> " +
                          $"{located} located | tracked {Tracker.Objects.Count} ({Tracker.VisibleCount} visible) | " +
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

        private void Update()
        {
            if (_paused) return;
            foreach (var o in Tracker.Prune(_removed)) Visualizer.Release(o);
            if (_eye == null) _eye = UnityEngine.Camera.main;
            TrackedObject focus = null;
            float best = 0f;
            if (_eye != null)
            {
                var head = _eye.transform;
                foreach (var o in Tracker.Objects)
                {
                    if (!o.visible) continue;
                    var offset = o.smoothedWorldPosition - head.position;
                    float distance = offset.magnitude;
                    if (distance < .2f || distance > 4f) continue;
                    float facing = Vector3.Dot(head.forward, offset / distance);
                    if (facing < .93f) continue; // about 22 degrees from where you are looking
                    float score = facing + (o == _focused ? .025f : 0f); // don't flicker between neighbours
                    if (score > best) { best = score; focus = o; }
                }
            }
            _focused = focus;
            foreach (var o in Tracker.Objects)
            {
                if (o == focus) Visualizer.Show(o);
                else Visualizer.Hide(o);
            }
        }

        private void OnDestroy()
        {
            if (Detector != null) Detector.OnDetections -= HandleDetections;
        }
    }
}
