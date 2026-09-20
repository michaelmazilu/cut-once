using System.Collections.Generic;
using CutOnce.Core.Vision;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>One real thing in the room, assembled from many noisy detections of it.</summary>
    public class TrackedObject
    {
        public int id;
        public int classId;
        public string className;
        public float confidence;
        public Vector3 worldPosition;          // newest raw measurement
        public Vector3 smoothedWorldPosition;  // what the visuals follow
        public Vector3 worldSize = new Vector3(0.25f, 0.25f, 0.25f);          // newest raw estimate
        public Vector3 smoothedWorldSize = new Vector3(0.25f, 0.25f, 0.25f);  // what the highlight is scaled to
        public float lastSeenTime;
        public int consecutiveHits;
        public int totalHits;
        public bool visible;                   // promoted past the hit threshold
        public GameObject visual;

        // ── the measured box ───────────────────────────────────────────────────────────────────────────────────
        // Detection is cheap and happens every frame; measuring a box costs a few hundred raycasts and happens a
        // few times a second. The smoother is what holds the box still in between, so the two can run at their own
        // rates instead of the slower one setting the pace for both.

        /// <summary>Holds this object's box across measurements: position, extents, turn and how sure the geometry is.</summary>
        public readonly BoxSmoother box = new BoxSmoother();

        /// <summary>True once a real fit has landed. Until then there is no box worth drawing.</summary>
        public bool hasMeasuredBox;
        public Vector3 measuredCentre;
        public Vector3 measuredSize;
        public float measuredYawDeg;
        public float geometryConfidence;
        public float lastMeasuredTime = -999f;
        /// <summary>Why the last attempt produced nothing, for the debug label.</summary>
        public string lastFitReason = "";

        // The newest detection of this object, kept so a measurement can be paced apart from the frame that saw it.
        // Objects do not move on their own, so a box that is a few frames stale still points at the right thing.
        public Rect lastBox;
        public Vector2 lastInputSize;
        public Pose lastPose;
        public bool hasLastBox;

        /// <summary>What the highlight should be drawn as: the measured box where there is one, the old estimate otherwise.</summary>
        public Vector3 DisplayCentre => hasMeasuredBox ? measuredCentre : smoothedWorldPosition;
        public Vector3 DisplaySize => hasMeasuredBox ? measuredSize : smoothedWorldSize;
        public float DisplayYawDeg => hasMeasuredBox ? measuredYawDeg : 0f;
    }

    /// <summary>
    /// Detections are per-frame and jittery; objects are not. This holds the difference.
    ///
    /// Association is same-class-and-near: a detection joins the nearest tracked object of the same
    /// class within <see cref="associationDistance"/>, otherwise it starts a new one. Position is
    /// smoothed with an EMA so labels sit still, an object must be seen
    /// <see cref="hitsBeforeVisible"/> times before it appears (kills one-frame false positives),
    /// and it survives <see cref="keepAliveSeconds"/> without being seen (kills the flicker when
    /// the model drops it for a frame or you glance away).
    /// </summary>
    public class TrackedObjectManager : MonoBehaviour
    {
        [Tooltip("Same class within this many metres is treated as the same physical object, close up. A miss makes a DUPLICATE, so it grows with range (below), where depth jitter does too.")]
        public float associationDistance = 0.25f;

        [Tooltip("Added to the above per metre of range. Two cans on one counter are 12 cm apart and must stay two; the same can across the room jitters by tens of centimetres and must stay one.")]
        public float associationPerMetre = 0.1f;

        [Tooltip("The most the two above may add up to.")]
        public float associationMaxDistance = 0.75f;

        [Tooltip("Detections needed before an object becomes visible. Suppresses one-frame false positives.")]
        public int hitsBeforeVisible = 3;

        [Tooltip("How long an object survives without being re-detected.")]
        public float keepAliveSeconds = 1.5f;

        [Tooltip("EMA weight for new measurements. Lower = steadier but slower to follow.")]
        [Range(0.05f, 1f)] public float positionSmoothing = 0.25f;

        [Tooltip("A measurement further than this from the tracked position is treated as a bad depth sample and ignored. Under the association reach, or it could never fire on a sighting that matched.")]
        public float jumpRejectDistance = 0.5f;

        public IReadOnlyList<TrackedObject> Objects => _objects;
        public int VisibleCount { get; private set; }

        private readonly List<TrackedObject> _objects = new();
        private int _nextId = 1;

        /// <summary>Older callers and diagnostics: position only, a box-ish default size.</summary>
        public TrackedObject Observe(in DetectedObject detection, Vector3 world)
            => Observe(detection, world, new Vector3(0.25f, 0.25f, 0.25f));

        /// <summary>Fold one located detection into the tracked set.</summary>
        public TrackedObject Observe(in DetectedObject detection, Vector3 world, Vector3 worldSize)
        {
            var now = Time.time;
            var match = FindNearest(detection.classId, world);

            if (match == null)
            {
                match = new TrackedObject
                {
                    id = _nextId++,
                    classId = detection.classId,
                    className = detection.className,
                    worldPosition = world,
                    smoothedWorldPosition = world,
                    worldSize = worldSize,
                    smoothedWorldSize = worldSize,
                };
                _objects.Add(match);
            }
            else
            {
                // A wild jump is nearly always a depth sample that found the wall behind the object,
                // not the object teleporting. Count the sighting, ignore the position.
                var jumped = Vector3.Distance(match.smoothedWorldPosition, world) > jumpRejectDistance;
                match.worldPosition = world;
                match.worldSize = worldSize;
                if (!jumped)
                {
                    match.smoothedWorldPosition = Vector3.Lerp(match.smoothedWorldPosition, world, positionSmoothing);
                    // Size rides the same smoothing and the same jump gate: a bad depth batch mis-sizes
                    // exactly when it mis-places, so both are rejected together.
                    match.smoothedWorldSize = Vector3.Lerp(match.smoothedWorldSize, worldSize, positionSmoothing);
                }
            }

            match.className = detection.className;
            match.confidence = Mathf.Max(match.confidence * 0.9f, detection.confidence);
            match.lastSeenTime = now;
            match.consecutiveHits++;
            match.totalHits++;
            if (!match.visible && match.consecutiveHits >= hitsBeforeVisible) match.visible = true;
            return match;
        }

        /// <summary>
        /// Fold one measured box into an object. A rejected fit is not nothing: it decays the geometry's confidence
        /// and, if they keep coming, retires the box — a hologram that is no longer being confirmed should fade, not
        /// sit there at full strength because the last good frame was convincing.
        /// </summary>
        public void Measure(TrackedObject o, FitResult fit)
        {
            var now = Time.time;
            var dt = Mathf.Clamp(now - o.lastMeasuredTime, 0f, 1f);
            o.lastMeasuredTime = now;
            o.lastFitReason = fit.Ok ? "" : fit.Reason;

            o.box.Update(fit, dt);
            if (!o.box.HasBox) return;

            o.hasMeasuredBox = true;
            var centre = o.box.Centre;
            var size = o.box.Size;
            o.measuredCentre = new Vector3(centre.X, centre.Y, centre.Z);
            o.measuredSize = new Vector3(size.X, size.Y, size.Z);
            o.measuredYawDeg = o.box.YawDeg;
            o.geometryConfidence = o.box.GeometryConfidence;

            // The measured centre is the better position, so association uses it too — otherwise the box and the
            // thing the tracker thinks it is following drift apart.
            o.smoothedWorldPosition = o.measuredCentre;
            o.smoothedWorldSize = o.measuredSize;
        }

        /// <summary>
        /// Forget the whole room. Everything here is a world-space position, which only means anything while the
        /// tracking origin has not moved under it; when that assumption breaks, the honest thing is to drop the lot
        /// and look again, not to keep boxes that describe where objects used to be relative to a vanished origin.
        /// Returns what was dropped, so visuals can be freed.
        /// </summary>
        public List<TrackedObject> Forget(List<TrackedObject> removed = null)
        {
            removed ??= new List<TrackedObject>();
            removed.Clear();
            removed.AddRange(_objects);
            _objects.Clear();
            VisibleCount = 0;
            return removed;
        }

        /// <summary>Retire anything not seen recently. Returns objects that died this call so visuals can be freed.</summary>
        public List<TrackedObject> Prune(List<TrackedObject> removed = null)
        {
            removed ??= new List<TrackedObject>();
            removed.Clear();
            var now = Time.time;
            for (var i = _objects.Count - 1; i >= 0; i--)
            {
                if (now - _objects[i].lastSeenTime <= keepAliveSeconds) continue;
                removed.Add(_objects[i]);
                _objects.RemoveAt(i);
            }

            VisibleCount = 0;
            foreach (var o in _objects) if (o.visible) VisibleCount++;
            return removed;
        }

        /// <summary>Call once per detection batch: anything not observed in it loses its streak.</summary>
        public void EndFrame(HashSet<int> observedIds)
        {
            foreach (var o in _objects)
                if (!observedIds.Contains(o.id)) o.consecutiveHits = 0;
        }

        /// <summary>How far away a sighting may be and still be the same thing: wider the further away it is.</summary>
        private float Reach(Vector3 world)
        {
            var eye = Camera.main;
            var range = eye != null ? Vector3.Distance(eye.transform.position, world) : 1f;
            return Mathf.Min(associationDistance + associationPerMetre * range, associationMaxDistance);
        }

        private TrackedObject FindNearest(int classId, Vector3 world)
        {
            TrackedObject best = null;
            var bestDistance = Reach(world);
            foreach (var o in _objects)
            {
                if (o.classId != classId) continue;
                var d = Vector3.Distance(o.smoothedWorldPosition, world);
                if (d > bestDistance) continue;
                bestDistance = d;
                best = o;
            }
            return best;
        }
    }
}
