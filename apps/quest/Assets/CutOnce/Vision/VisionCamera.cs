using System;
using Meta.XR;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// The Quest's colour passthrough camera, exposed for the vision pipeline.
    ///
    /// This does not replace Device/PcaFrameSource, which serves the cloud copilot. Live inference
    /// uses MRUK's documented asynchronous readback route: immediate Graphics.Blit of GetTexture
    /// can sample previous-frame pixels while GetCameraPose describes the newest frame. A leased
    /// immutable snapshot keeps captured pixels, pose, calibration and timing together.
    /// </summary>
    public class VisionCamera : MonoBehaviour
    {
        [Tooltip("Left is what Meta's own sample uses. Leave alone unless you have a reason.")]
        public PassthroughCameraAccess.CameraPositionType cameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        public Vector2Int requestedResolution = new Vector2Int(1280, 960);

        public PassthroughCameraAccess Access { get; private set; }
        public bool IsReady => isActiveAndEnabled && !_applicationPaused && Access != null && Access.isActiveAndEnabled && Access.IsPlaying;

        /// <summary>True only on frames where a genuinely new image arrived — don't re-infer stale pixels.</summary>
        public bool HasFreshFrame => Access != null && Access.IsUpdatedThisFrame;

        public Vector2Int Resolution => Access != null ? Access.CurrentResolution : Vector2Int.zero;
        public int CaptureGeneration { get; private set; }
        public CameraSnapshotStatus SnapshotStatus => _snapshot.Status;
        public string LastCaptureError => !string.IsNullOrEmpty(_metadataError) ? _metadataError : _snapshot.LastError;
        private readonly AsyncCameraSnapshot _snapshot = new AsyncCameraSnapshot();
        private PassthroughCameraAccess _observedAccess;
        private Texture _observedTexture;
        private Vector2Int _observedResolution;
        private DateTime _observedTimestamp, _lastQueuedTimestamp;
        private bool _wasReady, _applicationPaused;
        private string _metadataError = "";

        private void Awake()
        {
            // Reuse whatever is already in the scene (the Building Block prefab, or a teammate's rig)
            // before adding our own, so we never end up with two camera clients fighting.
            Access = FindAnyObjectByType<PassthroughCameraAccess>();
            if (Access != null) return;

            // Inactive while configuring: PassthroughCameraAccess.OnEnable already reads
            // CameraPosition/RequestedResolution, and AddComponent on an ACTIVE object runs
            // OnEnable synchronously — so assigning afterwards would be a no-op. Today the
            // defaults happen to match, which is exactly what makes this silent.
            var camGo = new GameObject("PassthroughCamera");
            camGo.transform.SetParent(transform, false);
            camGo.SetActive(false);
            Access = camGo.AddComponent<PassthroughCameraAccess>();
            Access.CameraPosition = cameraPosition;
            Access.RequestedResolution = requestedResolution;
            camGo.SetActive(true);
        }

        /// <summary>Borrowed changing texture for display only; live inference must use TryBeginSnapshot.</summary>
        public Texture GetTexture() => IsReady ? Access.GetTexture() : null;

        /// <summary>The colour camera's own pose at the current image's timestamp — NOT Camera.main.</summary>
        public Pose GetCameraPose() => Access.GetCameraPose();

        /// <summary>
        /// Enqueue after PCA.Update (-100), without yielding between metadata reads and request.
        /// Timestamp is a capture identifier only: it is not converted into Unity's clock domain.
        /// </summary>
        public bool TryBeginSnapshot()
        {
            RefreshStreamState();
            if (!IsReady || !HasFreshFrame || _snapshot.Status != CameraSnapshotStatus.Idle) return false;
            var access = Access;
            var frame = Time.frameCount;
            var timestamp = access.Timestamp;
            if (timestamp == default || timestamp == _lastQueuedTimestamp) return false;
            var texture = access.GetTexture();
            var resolution = access.CurrentResolution;
            var pose = access.GetCameraPose();
            var calibration = access.Intrinsics;
            if (!CapturedCameraProjection.TryCreate(pose, calibration.FocalLength, calibration.PrincipalPoint,
                calibration.SensorResolution, resolution, out var projection))
            { _metadataError = "Camera snapshot pose or calibration is invalid."; return false; }
            var stamp = new CameraCaptureStamp(pose, timestamp, resolution, CaptureGeneration,
                Time.realtimeSinceStartupAsDouble, projection);
            _metadataError = "";
            if (!_snapshot.TryBegin(texture, stamp)) return false;
            // PCA publishes metadata on the main thread. These checks reject an inconsistent
            // handoff; they are not a substitute for measuring native sensor/render pairing.
            if (!IsReady || Access != access || !access.IsUpdatedThisFrame || Time.frameCount != frame ||
                access.Timestamp != timestamp || access.CurrentResolution != resolution || access.GetTexture() != texture)
            {
                _metadataError = "Camera metadata changed while queuing its snapshot; waiting for readback drainage.";
                InvalidateSnapshots();
                return false;
            }
            _lastQueuedTimestamp = timestamp;
            return true;
        }

        public CameraSnapshotStatus PollSnapshot(double now)
        {
            RefreshStreamState();
            // Acquisition itself has the same strict default half-second freshness window as
            // surface paint. Inference publication includes this already-spent readback time.
            return _snapshot.Poll(now, CaptureGeneration, ObjectVisualizer.SurfaceFadeStartsAfterSeconds);
        }

        public bool TryTakeSnapshot(out CameraSnapshot snapshot)
        {
            PollSnapshot(Time.realtimeSinceStartupAsDouble);
            return _snapshot.TryTake(out snapshot);
        }

        public void ReleaseSnapshot() => _snapshot.Release();

        public void InvalidateSnapshots()
        {
            unchecked { ++CaptureGeneration; }
            _lastQueuedTimestamp = default;
            _snapshot.Invalidate();
        }

        private void Update() => PollSnapshot(Time.realtimeSinceStartupAsDouble);

        private void RefreshStreamState()
        {
            if (!IsReady)
            {
                if (_wasReady) InvalidateSnapshots();
                _wasReady = false;
                _observedAccess = null;
                _observedTexture = null;
                _observedTimestamp = default;
                return;
            }
            var texture = Access.GetTexture();
            var resolution = Access.CurrentResolution;
            var timestamp = Access.Timestamp;
            if (!_wasReady || _observedAccess != Access || _observedTexture != texture || _observedResolution != resolution ||
                timestamp == default || timestamp < _observedTimestamp)
                InvalidateSnapshots();
            _wasReady = true;
            _observedAccess = Access;
            _observedTexture = texture;
            _observedResolution = resolution;
            _observedTimestamp = timestamp;
        }

        private void OnDisable() => InvalidateSnapshots();

        private void OnApplicationPause(bool paused)
        {
            _applicationPaused = paused;
            InvalidateSnapshots();
        }

        private void OnDestroy() => _snapshot.Dispose();

        /// <summary>
        /// Ray through a viewport point, origin BOTTOM-LEFT. Detector boxes are top-left, so callers
        /// must flip y. Passing the pose cached at capture time is what keeps boxes from smearing
        /// when the head turns during inference.
        /// </summary>
        public Ray ViewportPointToRay(Vector2 viewport01, Pose cameraPose) => Access.ViewportPointToRay(viewport01, cameraPose);
    }
}
