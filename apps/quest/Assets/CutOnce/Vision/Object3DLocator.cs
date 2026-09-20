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

        [Tooltip("Camera-aligned dimensions for ordinary objects. People and tables have larger class-specific caps; rotated world bounds can be wider.")]
        public float minSizeM = 0.05f;
        public float maxSizeM = 1.2f;

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

        /// <summary>Position only; size discarded. Kept because diagnostics and older callers use it.</summary>
        public bool TryLocate(in DetectedObject detection, Pose cameraPose, out Vector3 world)
            => TryLocate(detection, cameraPose, out world, out _);

        /// <summary>
        /// World position AND estimated world size of a detection, or false if the room did not answer.
        /// <paramref name="cameraPose"/> must be the pose captured with the frame the detection came from.
        ///
        /// The size comes from the same two facts the position does: the detection's pixel box and the
        /// depth the samples agreed on. Rays through the box's edge midpoints intersect the SAME
        /// camera-forward depth plane, giving image-plane width and height in metres. Thickness is
        /// only a heuristic, not a recovered object mesh. Rotating those camera-aligned extents into
        /// conservative world bounds prevents an oblique view from cutting the highlight in half.
        /// The resulting AABB includes extra space at oblique angles and can include nearby clutter;
        /// a depth surface inside these bounds is not necessarily part of the detected object.
        /// </summary>
        public bool TryLocate(in DetectedObject detection, Pose cameraPose, out Vector3 world, out Vector3 size)
        {
            world = default;
            size = new Vector3(0.25f, 0.25f, 0.25f);
            LastAttempts++;
            var box = detection.boundingBox;
            var input = detection.inputSize;
            if (input.x <= 0f || input.y <= 0f) { LastFailureReason = "bad input size"; return false; }

            var centreRay = RayThrough(box.center, input, cameraPose);
            if (_raycast == null)                                    // no depth sensing: the room's surfaces instead of nothing at all
            {
                LastSuccesses++;
                var placed = LocateWithoutDepth(centreRay, out world);
                if (placed)
                {
                    var forwardDepth = Vector3.Dot(world - cameraPose.position, cameraPose.rotation * Vector3.forward);
                    size = SizeAt(forwardDepth, box, input, cameraPose, detection.className);
                }
                return placed;
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

                    var ray = RayThrough(pixel, input, cameraPose);
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
            size = SizeAt(depthAt, box, input, cameraPose, detection.className);
            LastSuccesses++;
            LastFailureReason = "";
            return true;
        }

        /// <summary>
        /// Rays through all four edge midpoints intersect one camera-forward depth plane. Equal
        /// distances ALONG the four rays would instead measure on a sphere and shrink off-axis boxes.
        /// </summary>
        private Vector3 SizeAt(float forwardDepth, Rect box, Vector2 inputSize, Pose cameraPose, string className)
        {
            if (!TryPointAtForwardDepth(RayThrough(new Vector2(box.xMin, box.center.y), inputSize, cameraPose), cameraPose, forwardDepth, out var left)
                || !TryPointAtForwardDepth(RayThrough(new Vector2(box.xMax, box.center.y), inputSize, cameraPose), cameraPose, forwardDepth, out var right)
                || !TryPointAtForwardDepth(RayThrough(new Vector2(box.center.x, box.yMin), inputSize, cameraPose), cameraPose, forwardDepth, out var top)
                || !TryPointAtForwardDepth(RayThrough(new Vector2(box.center.x, box.yMax), inputSize, cameraPose), cameraPose, forwardDepth, out var bottom))
                return Vector3.one * minSizeM;

            var cameraSize = EstimateCameraSize(Vector3.Distance(left, right), Vector3.Distance(top, bottom),
                className, minSizeM, maxSizeM);
            return WorldAlignedSize(cameraSize, cameraPose.rotation);
        }

        /// <summary>Intersect a ray with the plane that is forwardDepth metres in front of a captured camera.</summary>
        public static bool TryPointAtForwardDepth(Ray ray, Pose cameraPose, float forwardDepth, out Vector3 point)
        {
            point = default;
            if (!(forwardDepth > 0f) || float.IsInfinity(forwardDepth)) return false;
            var forward = cameraPose.rotation * Vector3.forward;
            var cosine = Vector3.Dot(ray.direction, forward);
            if (!(cosine > 0.1f)) return false;
            var distance = (forwardDepth - Vector3.Dot(ray.origin - cameraPose.position, forward)) / cosine;
            if (!(distance > 0f) || float.IsInfinity(distance)) return false;
            point = ray.GetPoint(distance);
            return true;
        }

        /// <summary>
        /// Bounds only: camera Z thickness is unobserved. Tables need room for their footprint, not
        /// just the thin tabletop seen edge-on, but this broader estimate can include nearby clutter.
        /// These are bounds for selecting depth surfaces, never a claim about an object's real shape.
        /// </summary>
        public static Vector3 EstimateCameraSize(float width, float height, string className, float minimum, float ordinaryMaximum)
        {
            var table = className == "dining table" || className == "diningtable" || className == "table";
            var widthCap = table ? Mathf.Max(ordinaryMaximum, 3f) : ordinaryMaximum;
            var heightCap = table ? widthCap : className == "person" ? Mathf.Max(ordinaryMaximum, 2f) : ordinaryMaximum;
            width = Mathf.Clamp(width, minimum, widthCap);
            height = Mathf.Clamp(height, minimum, heightCap);
            var depth = table
                ? Mathf.Clamp(Mathf.Max(width, height) * 0.65f, minimum, 1.5f)
                : Mathf.Clamp(Mathf.Min(width, height), minimum, 0.6f);
            return new Vector3(width, height, depth);
        }

        /// <summary>World-axis bounds that contain every corner of a rotated camera-aligned box.</summary>
        public static Vector3 WorldAlignedSize(Vector3 cameraSize, Quaternion cameraRotation)
        {
            var right = cameraRotation * Vector3.right;
            var up = cameraRotation * Vector3.up;
            var forward = cameraRotation * Vector3.forward;
            return new Vector3(
                Mathf.Abs(right.x) * cameraSize.x + Mathf.Abs(up.x) * cameraSize.y + Mathf.Abs(forward.x) * cameraSize.z,
                Mathf.Abs(right.y) * cameraSize.x + Mathf.Abs(up.y) * cameraSize.y + Mathf.Abs(forward.y) * cameraSize.z,
                Mathf.Abs(right.z) * cameraSize.x + Mathf.Abs(up.z) * cameraSize.y + Mathf.Abs(forward.z) * cameraSize.z);
        }


        /// <summary>Box pixel (top-left origin) -> viewport (bottom-left origin) -> world ray. The y flip is mandatory.</summary>
        private Ray RayThrough(Vector2 pixel, Vector2 inputSize, Pose cameraPose)
        {
            var viewport = new Vector2(pixel.x / inputSize.x, 1f - pixel.y / inputSize.y);
            return _camera.ViewportPointToRay(viewport, cameraPose);
        }
    }
}
