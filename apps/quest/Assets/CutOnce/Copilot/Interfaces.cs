using System;
using UnityEngine;

namespace CutOnce.Copilot
{
    public enum CopilotActivity { Idle, Listening, Thinking }

    /// <summary>One camera frame, already encoded, with everything needed to project 3D into it.</summary>
    public readonly struct CameraFrame
    {
        public readonly byte[] Jpeg;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly CameraIntrinsics Intrinsics;
        public readonly DateTime CapturedAtUtc;
        /// <summary>
        /// World point → viewport point for THIS frame's camera: (0,0) bottom-left, (1,1) top-right, z = depth.
        /// On the device it is Meta's WorldToViewportPoint with the pose cached at capture; for recorded frames it
        /// is <see cref="CameraIntrinsics.Pinhole"/>.
        /// </summary>
        public readonly Func<Vector3, Vector3> WorldToViewport;

        public CameraFrame(byte[] jpeg, Vector3 position, Quaternion rotation, CameraIntrinsics intrinsics, DateTime capturedAtUtc,
                           Func<Vector3, Vector3> worldToViewport)
        {
            Jpeg = jpeg; Position = position; Rotation = rotation; Intrinsics = intrinsics; CapturedAtUtc = capturedAtUtc;
            WorldToViewport = worldToViewport;
        }

        public bool IsValid => Jpeg != null && Jpeg.Length > 0 && Intrinsics.Width > 0;
    }

    /// <summary>Pinhole intrinsics in image pixels with the origin top-left (the OpenCV convention ExpectedViewRenderer uses).</summary>
    [Serializable]
    public struct CameraIntrinsics
    {
        public int width, height;
        public float fx, fy, cx, cy;

        public int Width => width;
        public int Height => height;

        /// <summary>Falls back to a pinhole model from the vertical field of view when the device does not report intrinsics.</summary>
        public static CameraIntrinsics FromFov(int width, int height, float verticalFovDegrees)
        {
            float fy = height * 0.5f / Mathf.Tan(verticalFovDegrees * 0.5f * Mathf.Deg2Rad);
            return new CameraIntrinsics { width = width, height = height, fx = fy, fy = fy, cx = width * 0.5f, cy = height * 0.5f };
        }

        /// <summary>
        /// Meta's intrinsics (PassthroughCameraAccess.Intrinsics, MRUK 205) are in sensor pixels with the origin
        /// bottom-left, and the image is a centred crop of the sensor scaled to the current resolution
        /// (its CalcSensorCropRegion). This converts them to image pixels with the origin top-left, so
        /// <see cref="Pinhole"/> and ExpectedViewRenderer agree with Meta's WorldToViewportPoint.
        /// </summary>
        public static CameraIntrinsics FromMeta(Vector2 focalLength, Vector2 principalPoint, Vector2Int sensorResolution, Vector2Int currentResolution)
        {
            Vector2 sensor = sensorResolution, current = currentResolution;
            Vector2 scale = new Vector2(current.x / sensor.x, current.y / sensor.y);
            scale /= Mathf.Max(scale.x, scale.y);
            var crop = new Rect(sensor.x * (1f - scale.x) * 0.5f, sensor.y * (1f - scale.y) * 0.5f, sensor.x * scale.x, sensor.y * scale.y);
            float kx = current.x / crop.width, ky = current.y / crop.height;
            return new CameraIntrinsics
            {
                width = currentResolution.x, height = currentResolution.y,
                fx = focalLength.x * kx, fy = focalLength.y * ky,
                cx = (principalPoint.x - crop.x) * kx,
                cy = current.y - (principalPoint.y - crop.y) * ky,
            };
        }

        /// <summary>
        /// Projects a world point through a camera at <paramref name="cameraPosition"/> looking down its +Z (+Y up),
        /// returning a Unity viewport point like Meta's WorldToViewportPoint: (0,0) bottom-left, z = depth in metres.
        /// </summary>
        public Vector3 Pinhole(Vector3 world, Vector3 cameraPosition, Quaternion cameraRotation)
        {
            Vector3 c = Quaternion.Inverse(cameraRotation) * (world - cameraPosition);
            float column = cx + fx * c.x / c.z;
            float row = cy - fy * c.y / c.z;
            return new Vector3(column / width, 1f - row / height, c.z);
        }
    }

    /// <summary>
    /// Where frames come from. FixtureFrameSource replays a recorded frame in the Editor; on the device
    /// Device/PcaFrameSource reads Meta's Passthrough Camera API. It lives outside this assembly so this one
    /// needs no Meta reference and can be tested on its own.
    /// </summary>
    public interface ICameraFrameSource
    {
        bool IsReady { get; }
        /// <summary>Grabs the newest frame. Called on the main thread; JPEG encoding may happen off it.</summary>
        CameraFrame Capture();
    }

    /// <summary>Push-to-talk. The device implementation (Device/QuestPushToTalk) reads Meta's OVRInput.</summary>
    public interface IPushToTalk
    {
        bool Down { get; }   // pressed this frame
        bool Up { get; }     // released this frame
    }

    /// <summary>What the user was pointing at, and the step they were on, when they asked.</summary>
    public readonly struct Selection
    {
        public readonly string PartId, Source, StepId;

        public Selection(string partId, string source, string stepId)
        {
            PartId = partId;
            Source = string.IsNullOrEmpty(partId) ? "none" : source;
            StepId = stepId;
        }

        public static Selection Of(ICopilotHost host) => new Selection(host.SelectedPartId, host.SelectionSource, host.CurrentStepId);
    }

    /// <summary>A part as the headset knows it, for projection. Implemented over A2's part index.</summary>
    public interface IProjectablePart
    {
        string PartId { get; }
        string State { get; }        // missing | built | wrong
        Bounds WorldBounds { get; }  // axis-aligned world bounds of the part's renderer
    }

    /// <summary>
    /// The four things the copilot needs from the rest of the app. A2 owns the real implementation;
    /// this interface exists so pillar C compiles and runs against a stub until that lands.
    /// </summary>
    public interface ICopilotHost
    {
        string AssemblyId { get; }
        int PlanRevision { get; }
        int StateVersion { get; }
        string Mode { get; }              // upload | overlay
        string SelectedPartId { get; }    // null when the ray hits nothing
        string SelectionSource { get; }   // controller_ray | gaze | none
        string CurrentStepId { get; }

        System.Collections.Generic.IReadOnlyList<IProjectablePart> PartsForProjection();

        /// <summary>Pulse or path-highlight these parts. Called the moment the answer arrives, before the audio.</summary>
        void Highlight(string[] partIds, string style);

        /// <summary>Highlight real objects found by build mode. Their IDs are o1, o2, ... rather than plan part IDs.</summary>
        void HighlightTwins(string[] twinIds, string style);

        /// <summary>Show immediate hold-to-talk feedback independently of the eventual answer.</summary>
        void ShowCopilotActivity(CopilotActivity activity);

        /// <summary>Show the answer text and the source card on the HUD.</summary>
        void ShowAnswer(CopilotResponseDto response);

        /// <summary>A spoken command already applied by the server. Show a 2 s Undo toast, do NOT append an event.</summary>
        void OnActionApplied(CopilotActionDto action);

        /// <summary>Spoken "next" / "back". Headset-local: move the step view, write nothing.</summary>
        void StepNav(string direction);
    }
}
