using System;
using CutOnce.Placement;
using Meta.XR;
using UnityEngine;

namespace CutOnce.Device
{
    /// <summary>Real environment depth only. No room-plane, collider or fixed-distance fallback.</summary>
    public sealed class QuestPlacementSurface : MonoBehaviour, IPlacementSurfaceSource
    {
        EnvironmentRaycastManager manager;
        PassthroughCameraAccess cameraSource;
        DateTime lastImage;
        double observedAt;
        Vector3 eye;
        readonly System.Diagnostics.Stopwatch budget = new System.Diagnostics.Stopwatch();

        public void Init(EnvironmentRaycastManager depth, PassthroughCameraAccess camera)
        { manager = depth; cameraSource = camera; }

        public bool TryFrame(out Vector3 position, out double timestamp)
        {
            position = default; timestamp = 0;
            if (manager == null || !manager.isActiveAndEnabled || cameraSource == null || !cameraSource.IsPlaying ||
                !EnvironmentRaycastManager.IsSupported || (OVRManager.instance != null && !OVRManager.tracker.isPositionTracked)) return false;
            // Only accept newly delivered camera frames. Never refresh an old image's timestamp on polling.
            if (cameraSource.IsUpdatedThisFrame && cameraSource.Timestamp != default && cameraSource.Timestamp > lastImage)
            {
                double age = (DateTime.UtcNow - cameraSource.Timestamp).TotalSeconds;
                if (age < 0 || age > SurfacePlacementCheck.MaxAge) return false;
                lastImage = cameraSource.Timestamp;
                observedAt = Time.unscaledTimeAsDouble - age; // preserve capture age across wall/monotonic clocks
                eye = cameraSource.GetCameraPose().position;
            }
            if (lastImage == default || Time.unscaledTimeAsDouble - observedAt > SurfacePlacementCheck.MaxAge) return false;
            position = eye; timestamp = observedAt; budget.Restart();
            return true;
        }

        public bool Raycast(Ray ray, out Vector3 point, out Vector3 normal, out float confidence)
        {
            point = normal = default; confidence = 0;
            if (budget.Elapsed.TotalMilliseconds > 3 || manager == null ||
                !manager.Raycast(ray, out var hit, 3f) || hit.status != EnvironmentRaycastHitStatus.Hit) return false;
            point = hit.point; normal = hit.normal; confidence = hit.normalConfidence;
            return true;
        }

        void OnDisable() { lastImage = default; }
    }
}
