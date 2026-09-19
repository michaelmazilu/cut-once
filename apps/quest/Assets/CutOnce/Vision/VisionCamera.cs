using Meta.XR;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// The Quest's colour passthrough camera, exposed for the vision pipeline.
    ///
    /// This deliberately does NOT replace Device/PcaFrameSource — that one serves the cloud copilot
    /// and pays for a blocking GPU readback plus a JPEG encode every frame. For on-device inference
    /// we want the opposite: <see cref="PassthroughCameraAccess.GetTexture"/> hands back a GPU
    /// texture that can go straight into a tensor without the pixels ever touching the CPU. Both
    /// read the same underlying MRUK component, so nothing is duplicated and nothing is rewritten.
    /// </summary>
    public class VisionCamera : MonoBehaviour
    {
        [Tooltip("Left is what Meta's own sample uses. Leave alone unless you have a reason.")]
        public PassthroughCameraAccess.CameraPositionType cameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
        public Vector2Int requestedResolution = new Vector2Int(1280, 960);

        public PassthroughCameraAccess Access { get; private set; }
        public bool IsReady => Access != null && Access.IsPlaying;

        /// <summary>True only on frames where a genuinely new image arrived — don't re-infer stale pixels.</summary>
        public bool HasFreshFrame => Access != null && Access.IsUpdatedThisFrame;

        public Vector2Int Resolution => Access != null ? Access.CurrentResolution : Vector2Int.zero;

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

        public Texture GetTexture() => IsReady ? Access.GetTexture() : null;

        /// <summary>The colour camera's own pose at the current image's timestamp — NOT Camera.main.</summary>
        public Pose GetCameraPose() => Access.GetCameraPose();

        /// <summary>
        /// Ray through a viewport point, origin BOTTOM-LEFT. Detector boxes are top-left, so callers
        /// must flip y. Passing the pose cached at capture time is what keeps boxes from smearing
        /// when the head turns during inference.
        /// </summary>
        public Ray ViewportPointToRay(Vector2 viewport01, Pose cameraPose) => Access.ViewportPointToRay(viewport01, cameraPose);
    }
}
