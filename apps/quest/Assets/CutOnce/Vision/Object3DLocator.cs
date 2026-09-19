using System.Collections.Generic;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// Turns a 2D detection box into a place in the real room.
    ///
    /// Route: box pixel -> viewport point (y flipped) -> camera ray built from the pose CACHED AT
    /// CAPTURE -> EnvironmentRaycastManager against the Quest's depth sensing -> world point.
    ///
    /// Three things here are load-bearing and each one fails quietly if you get it wrong:
    ///
    /// 1. NOT the MRUK scene mesh. MRUKRoom.Raycast only tests anchor planes and volume boxes, so a
    ///    ray through a bottle on a desk returns the desk — or the wall behind it. Loose objects,
    ///    the entire point of detection, are invisible to it.
    /// 2. NOT `origin + direction * depth` from a raw depth texture. Meta's own AI Building Block
    ///    does this, but that depth is view-space Z, not distance along the ray, so it
    ///    under-estimates by cos(theta) off-axis. We take the hit point directly from the raycast,
    ///    which sidesteps the conversion entirely.
    /// 3. NOT a single centre pixel. A bounding box always contains background around the object,
    ///    and background is farther away, so we sample a grid and take a LOWER-biased percentile —
    ///    biased toward the near surface, which is the object itself.
    /// 4. NOT ray lengths. Samples are compared as DEPTH ALONG THE CAMERA'S FORWARD AXIS. Rays
    ///    through different pixels point in different directions, so against a flat wall the
    ///    off-centre ones are simply longer; averaging those lengths and replaying them along the
    ///    centre ray puts the object behind the wall and mis-sizes the box. We project each hit
    ///    onto forward, take the percentile there, then convert back to a distance along the centre
    ///    ray with t = depth / dot(centreDir, forward).
    /// </summary>
    public class Object3DLocator : MonoBehaviour
    {
        [Tooltip("Grid resolution across the inner part of the box. 3 = 9 samples.")]
        [Range(1, 5)] public int sampleGrid = 3;

        [Tooltip("Fraction of the box the sample grid spans. Keeps samples off the edges, where background leaks in.")]
        [Range(0.2f, 1f)] public float sampleSpan = 0.5f;

        [Tooltip("Fraction of samples that must hit before we trust the result.")]
        [Range(0.1f, 1f)] public float minValidFraction = 0.4f;

        [Tooltip("Percentile of hit distances to use. Below 0.5 biases toward the near surface (the object, not the wall behind it).")]
        [Range(0f, 1f)] public float distancePercentile = 0.4f;

        public float minDistance = 0.2f;   // Quest depth is unreliable closer than this
        public float maxDistance = 6f;     // official guidance: limited accuracy beyond ~4m

        [Tooltip("Used only where there is no depth sensing (running from the Editor over Link): how far down the ray to put an object the room's own planes did not catch.")]
        public float fallbackDistance = 2f;

        public int LastAttempts { get; private set; }
        public int LastSuccesses { get; private set; }
        public string LastFailureReason { get; private set; } = "";

        private VisionCamera _camera;
        private EnvironmentRaycastManager _raycast;
        private readonly List<float> _distances = new();

        public bool IsSupported => EnvironmentRaycastManager.IsSupported;

        private void Awake()
        {
            _camera = GetComponent<VisionCamera>() ?? FindAnyObjectByType<VisionCamera>();
            if (!EnvironmentRaycastManager.IsSupported)
            {
                LastFailureReason = "EnvironmentRaycastManager not supported on this device/simulator";
                Debug.LogWarning("[Vision] " + LastFailureReason);
                return;
            }
            _raycast = FindAnyObjectByType<EnvironmentRaycastManager>();
            if (_raycast == null) _raycast = gameObject.AddComponent<EnvironmentRaycastManager>();
        }

        /// <summary>
        /// Where there is no depth sensing — running from the Editor over Meta Horizon Link, or a simulator without
        /// it — an object still gets a place, so the room lights up and the pipeline can be watched end to end. It
        /// lands on the room's own surfaces (MRUK's planes and volumes), which means the desk UNDER the bottle or the
        /// wall BEHIND it, not the bottle: near enough to see it working, never good enough to build from. Says so.
        /// </summary>
        bool LocateWithoutDepth(Ray centreRay, out Vector3 world)
        {
            if (!_warnedNoDepth)
            {
                _warnedNoDepth = true;
                Debug.LogWarning("[Vision] no depth sensing here: objects are placed on the room's surfaces (the desk under a thing, or the wall behind it), " +
                                 "not on the thing itself. Real positions need the headset.");
            }
            var room = MRUK.Instance != null ? MRUK.Instance.GetCurrentRoom() : null;
            if (room != null && room.Raycast(centreRay, maxDistance, out var hit)) { world = hit.point; return true; }
            world = centreRay.origin + centreRay.direction * fallbackDistance;
            return true;
        }

        bool _warnedNoDepth;

        /// <summary>
        /// World position of a detection, or false if the room did not answer. <paramref name="cameraPose"/>
        /// must be the pose captured with the frame the detection came from.
        /// </summary>
        public bool TryLocate(in DetectedObject detection, Pose cameraPose, out Vector3 world)
        {
            world = default;
            LastAttempts++;
            var box = detection.boundingBox;
            var size = detection.inputSize;
            if (size.x <= 0f || size.y <= 0f) { LastFailureReason = "bad input size"; return false; }

            var centreRay = RayThrough(box.center, size, cameraPose);
            if (_raycast == null)                                    // no depth sensing: the room's surfaces instead of nothing at all
            {
                LastSuccesses++;
                return LocateWithoutDepth(centreRay, out world);
            }
            var forward = cameraPose.rotation * Vector3.forward;

            _distances.Clear();
            var taken = 0;
            for (var gy = 0; gy < sampleGrid; gy++)
            {
                for (var gx = 0; gx < sampleGrid; gx++)
                {
                    taken++;
                    var t = sampleGrid == 1
                        ? new Vector2(0.5f, 0.5f)
                        : new Vector2(gx / (float)(sampleGrid - 1), gy / (float)(sampleGrid - 1));
                    // Pull the grid into the middle of the box so edge pixels (background) don't dominate.
                    var inset = 0.5f - sampleSpan * 0.5f;
                    var pixel = new Vector2(
                        box.xMin + box.width * Mathf.Lerp(inset, 1f - inset, t.x),
                        box.yMin + box.height * Mathf.Lerp(inset, 1f - inset, t.y));

                    var ray = RayThrough(pixel, size, cameraPose);
                    if (!_raycast.Raycast(ray, out var hit, maxDistance)) continue;
                    if (hit.status != EnvironmentRaycastHitStatus.Hit) continue;

                    // Depth along the camera's forward axis — the one coordinate that is comparable
                    // between rays pointing in different directions.
                    var depth = Vector3.Dot(hit.point - cameraPose.position, forward);
                    if (depth < minDistance || depth > maxDistance) continue;
                    _distances.Add(depth);
                }
            }

            if (_distances.Count == 0 || _distances.Count < taken * minValidFraction)
            {
                LastFailureReason = $"only {_distances.Count}/{taken} depth samples valid";
                return false;
            }

            _distances.Sort();
            var index = Mathf.Clamp(Mathf.RoundToInt((_distances.Count - 1) * distancePercentile), 0, _distances.Count - 1);
            var depthAt = _distances[index];

            // Convert that depth back into a distance along the centre ray. Guard the degenerate
            // case of a ray almost perpendicular to forward, where the division explodes.
            var cosine = Vector3.Dot(centreRay.direction, forward);
            if (cosine < 0.1f) { LastFailureReason = "detection too far off-axis to place"; return false; }
            world = centreRay.GetPoint(depthAt / cosine);
            LastSuccesses++;
            LastFailureReason = "";
            return true;
        }

        /// <summary>Box pixel (top-left origin) -> viewport (bottom-left origin) -> world ray. The y flip is mandatory.</summary>
        private Ray RayThrough(Vector2 pixel, Vector2 inputSize, Pose cameraPose)
        {
            var viewport = new Vector2(pixel.x / inputSize.x, 1f - pixel.y / inputSize.y);
            return _camera.ViewportPointToRay(viewport, cameraPose);
        }
    }
}
