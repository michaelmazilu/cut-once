using System.Collections.Generic;
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
