using UnityEngine;

namespace CutOnce.Placement
{
    /// <summary>Measured environment surfaces, never Unity colliders or saved room planes.</summary>
    public interface IPlacementSurfaceSource
    {
        bool Raycast(Ray ray, out Vector3 point, out Vector3 normal, out float confidence);
    }

    public enum SurfacePlacementState { Unavailable, Mismatch, Aligning, Matches }

    /// <summary>
    /// Checks the VISIBLE geometry of a known box against independent depth measurements. This is not
    /// instance recognition, a recovered six-axis object pose, or evidence of physical attachment.
    /// Two nonparallel faces are required: one desk/wall plane cannot confirm a box. No pose is invented
    /// from a detection centre, and missing/occluded/low-confidence measurements cannot count as matches.
    /// </summary>
    public sealed class SurfacePlacementCheck
    {
        public SurfacePlacementState State { get; private set; }
        public int Faces { get; private set; }
        public float MaxError { get; private set; }
        public const float DistanceTolerance = .05f, NormalTolerance = 12f;
        public const double MaxAge = .25, HoldSeconds = .5;
        double since = double.NaN, lastSample = double.NegativeInfinity;
        Matrix4x4 previousMatrix;
        Bounds previousBounds;
        bool hasTarget;

        public void Reset()
        {
            State = SurfacePlacementState.Unavailable;
            since = double.NaN; lastSample = double.NegativeInfinity; hasTarget = false;
            Faces = 0; MaxError = 0;
        }

        public SurfacePlacementState Evaluate(IPlacementSurfaceSource source, Vector3 eye,
            Matrix4x4 localToWorld, Bounds bounds, double sampleTime, double now)
        {
            if (hasTarget && (previousMatrix != localToWorld || previousBounds != bounds)) Reset();
            previousMatrix = localToWorld; previousBounds = bounds; hasTarget = true;
            if (source == null || !Finite(eye) || double.IsNaN(sampleTime) || double.IsInfinity(sampleTime) ||
                double.IsNaN(now) || double.IsInfinity(now) || sampleTime > now || now - sampleTime > MaxAge ||
                sampleTime < lastSample)
                return Unavailable();
            if (sampleTime == lastSample) return State; // a repeated image cannot advance the hold
            if (sampleTime - lastSample > MaxAge) since = double.NaN;
            lastSample = sampleTime;
            Faces = 0; MaxError = 0;
            bool mismatch = false;
            // 3 axes, at most one front-facing side each, 9 rays per side: at most 27 rays per sample.
            for (int axis = 0; axis < 3; axis++)
            {
                int u = (axis + 1) % 3, v = (axis + 2) % 3;
                // Tiny faces cannot give independent, reliable depth evidence at Quest resolution.
                if (bounds.size[u] < .08f || bounds.size[v] < .08f) continue;
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Vector3 n = Vector3.zero; n[axis] = sign;
                    var normal = localToWorld.MultiplyVector(n).normalized;
                    var centre = bounds.center; centre[axis] += bounds.extents[axis] * sign;
                    var face = localToWorld.MultiplyPoint3x4(centre);
                    var toEye = eye - face;
                    if (toEye.magnitude < .25f || toEye.magnitude > 2.5f || Vector3.Dot(normal, toEye.normalized) < .35f) continue;
                    int valid = 0, matched = 0;
                    for (int y = -1; y <= 1; y++)
                    for (int x = -1; x <= 1; x++)
                    {
                        var local = centre;
                        local[u] += x * bounds.extents[u] * .65f;
                        local[v] += y * bounds.extents[v] * .65f;
                        var expected = localToWorld.MultiplyPoint3x4(local);
                        var ray = new Ray(eye, (expected - eye).normalized);
                        if (!source.Raycast(ray, out var hit, out var measuredNormal, out var confidence) ||
                            !Finite(hit) || !Finite(measuredNormal) || !PlacementSettings.Finite(confidence) ||
                            confidence < .7f || confidence > 1 || measuredNormal.sqrMagnitude < .5f) continue;
                        valid++;
                        float error = Vector3.Distance(hit, expected);
                        MaxError = Mathf.Max(MaxError, error);
                        // Normal sign is immaterial; the SDK may orient normals toward the sensor.
                        float angle = Mathf.Acos(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(measuredNormal.normalized, normal)))) * Mathf.Rad2Deg;
                        if (error <= DistanceTolerance && angle <= NormalTolerance) matched++;
                    }
                    if (valid < 7) continue;
                    Faces++;
                    if (matched < 8) mismatch = true; // 8/9 must match; missing rays never count as evidence
                }
            }
            if (Faces < 2) return Unavailable();
            if (mismatch) { since = double.NaN; return State = SurfacePlacementState.Mismatch; }
            if (double.IsNaN(since)) since = sampleTime;
            return State = sampleTime - since >= HoldSeconds ? SurfacePlacementState.Matches : SurfacePlacementState.Aligning;
        }

        SurfacePlacementState Unavailable() { since = double.NaN; return State = SurfacePlacementState.Unavailable; }
        static bool Finite(Vector3 p) => PlacementSettings.Finite(p.x) && PlacementSettings.Finite(p.y) && PlacementSettings.Finite(p.z);
    }
}
