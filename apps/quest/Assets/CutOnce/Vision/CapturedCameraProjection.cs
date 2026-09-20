using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// Immutable projection from a world surface into the ORIGINAL captured RGB image, with
    /// normalized mask UV measured from its TOP LEFT. Cache this alongside the image's pose and
    /// inference input; never rebuild it from the current headset or rendering-eye pose.
    ///
    /// Calibration follows MRUK 205 PassthroughCameraAccess.WorldToViewportPoint and its central
    /// sensor crop. Supply public Intrinsics.FocalLength/PrincipalPoint/SensorResolution and the
    /// captured CurrentResolution. GetCameraPose already includes the sensor's LensOffset.
    /// Assumes pose/calibration and RGB pixels belong to the same capture. This helper cannot
    /// establish that pairing, synchronize RGB/depth timestamps, or undo model padding.
    /// </summary>
    public readonly struct CapturedCameraProjection
    {
        public bool IsValid { get; }
        public Pose CameraPose { get; }
        public Rect SensorCrop { get; }
        public Vector2Int ImageResolution { get; }

        /// <summary>
        /// Multiply by float4(worldPosition, 1), then divide xy by w for top-left image UV.
        /// Both z and w are positive captured-camera forward depth, NOT Unity clip-space depth.
        /// Shader callers must reject nonpositive depth and UV outside [0,1] before sampling.
        /// </summary>
        public Matrix4x4 WorldToMask { get; }

        private CapturedCameraProjection(Pose pose, Rect crop, Vector2Int resolution, Matrix4x4 matrix)
        {
            IsValid = true;
            CameraPose = pose;
            SensorCrop = crop;
            ImageResolution = resolution;
            WorldToMask = matrix;
        }

        public static bool TryCreate(Pose capturedCameraPose, Vector2 focalLengthPixels,
            Vector2 principalPointPixels, Vector2Int sensorResolution, Vector2Int imageResolution,
            out CapturedCameraProjection projection)
        {
            projection = default;
            if (!Finite(capturedCameraPose.position) || !Finite(focalLengthPixels) ||
                !Finite(principalPointPixels) || focalLengthPixels.x <= 0f || focalLengthPixels.y <= 0f ||
                sensorResolution.x <= 0 || sensorResolution.y <= 0 || imageResolution.x <= 0 || imageResolution.y <= 0)
                return false;

            var q = capturedCameraPose.rotation;
            if (!Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w)) return false;
            var lengthSquared = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (!Finite(lengthSquared) || lengthSquared < 1e-12f) return false;
            var inverseLength = 1f / Mathf.Sqrt(lengthSquared);
            q = new Quaternion(q.x * inverseLength, q.y * inverseLength, q.z * inverseLength, q.w * inverseLength);
            var pose = new Pose(capturedCameraPose.position, q);

            // The stream is a centrally cropped sensor image, not necessarily the full sensor.
            // This is the same aspect-ratio crop used by the SDK's public viewport/ray methods.
            var scale = new Vector2((float)imageResolution.x / sensorResolution.x,
                (float)imageResolution.y / sensorResolution.y);
            scale /= Mathf.Max(scale.x, scale.y);
            var crop = new Rect(sensorResolution.x * (1f - scale.x) * .5f,
                sensorResolution.y * (1f - scale.y) * .5f,
                sensorResolution.x * scale.x, sensorResolution.y * scale.y);
            if (!(crop.width > 0f) || !(crop.height > 0f)) return false;

            var cameraToMask = Matrix4x4.zero;
            cameraToMask.m00 = focalLengthPixels.x / crop.width;
            cameraToMask.m02 = (principalPointPixels.x - crop.x) / crop.width;
            cameraToMask.m11 = -focalLengthPixels.y / crop.height;
            cameraToMask.m12 = 1f - (principalPointPixels.y - crop.y) / crop.height;
            cameraToMask.m22 = 1f;
            cameraToMask.m32 = 1f;
            if (!(cameraToMask.m00 > 0f) || !(cameraToMask.m11 < 0f)) return false;
            var worldToCamera = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one).inverse;
            var worldToMask = cameraToMask * worldToCamera;
            for (var i = 0; i < 16; i++) if (!Finite(worldToMask[i])) return false;

            projection = new CapturedCameraProjection(pose, crop, imageResolution, worldToMask);
            return true;
        }

        /// <summary>Reject behind-camera, off-image, or nonfinite surfaces; never clamp onto a mask edge.</summary>
        public bool TryProject(Vector3 worldPosition, out Vector2 topLeftMaskUv)
        {
            topLeftMaskUv = default;
            if (!IsValid || !Finite(worldPosition)) return false;
            var p = WorldToMask * new Vector4(worldPosition.x, worldPosition.y, worldPosition.z, 1f);
            if (!Finite(p.x) || !Finite(p.y) || !Finite(p.w) || p.w <= 1e-5f) return false;
            var uv = new Vector2(p.x / p.w, p.y / p.w);
            if (!Finite(uv) || uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) return false;
            topLeftMaskUv = uv;
            return true;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
