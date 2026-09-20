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
        public float lastSeenTime;            // legacy scaled-time estimate; live aging uses the acquisition clock below
        public double lastAcquiredAtRealtimeSeconds = double.NaN; // NaN means a legacy/recorded caller supplied no live timestamp
        public int consecutiveHits;
        public int totalHits;
        public bool visible;                   // promoted past the hit threshold
        public GameObject visual;

        /// <summary>Live freshness includes inference delay. Legacy callers retain their existing scaled-time behavior.</summary>
        public float AgeAt(double realtimeNow, float legacyNow)
        {
            if (double.IsNaN(lastAcquiredAtRealtimeSeconds)) return Mathf.Max(0f, legacyNow - lastSeenTime);
            var age = realtimeNow - lastAcquiredAtRealtimeSeconds;
            return double.IsNaN(age) || double.IsInfinity(age) || age < 0d ? float.PositiveInfinity : (float)age;
        }
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

        /// <summary>Older callers and diagnostics: position only, a box-ish default size. Null for invalid geometry.</summary>
        public TrackedObject Observe(in DetectedObject detection, Vector3 world)
            => Observe(detection, world, new Vector3(0.25f, 0.25f, 0.25f));

        /// <summary>Fold one located detection into the tracked set, or return null for invalid geometry.</summary>
        public TrackedObject Observe(in DetectedObject detection, Vector3 world, Vector3 worldSize)
            => Observe(detection, world, worldSize, null);

        /// <summary>
        /// Associate at most one detection with each track per batch. Pass the caller's observed-ID
        /// set, adding each returned ID to it before the next call. Otherwise two nearby bottles
        /// within the association radius can both update one track and one physical object vanishes.
        /// Input detections must already be de-duplicated by NMS; this is one-to-one spatial tracking,
        /// not a second object detector or proof that overlapping detections are distinct objects.
        /// Returns null without changing tracks or consuming an ID if position/size is nonfinite
        /// or any size component is nonpositive. Only add a nonnull result's ID to the observed set.
        /// </summary>
        public TrackedObject Observe(in DetectedObject detection, Vector3 world, Vector3 worldSize, ISet<int> observedIds,
            DetectionFrameTiming frameTiming = default)
        {
            // NaN distances otherwise compare as if in range and overwrite an arbitrary track.
            // Zero/negative world coordinates are valid; sizes must be strictly positive.
            if (!Finite(world) || !Finite(worldSize) || worldSize.x <= 0f || worldSize.y <= 0f || worldSize.z <= 0f)
                return null;
            var now = Time.time;
            var match = FindNearest(detection.classId, world, observedIds);

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
                // A repeated or out-of-order live frame must not reconfirm or refresh a newer track.
                if (frameTiming.isLiveCapture && !double.IsNaN(match.lastAcquiredAtRealtimeSeconds) &&
                    frameTiming.acquiredAtRealtimeSeconds <= match.lastAcquiredAtRealtimeSeconds)
                    return match;
                // A wild jump is nearly always a depth sample that found the wall behind the object,
                // not the object teleporting. It is not evidence that the OLD geometry is still fresh:
                // do not refresh its timestamp, confidence, hit counts, or raw/smoothed geometry.
                // Return the association for the caller's one-to-one batch reservation, but break
                // its confirmation streak exactly as an unobserved track would lose its streak.
                var jumped = Vector3.Distance(match.smoothedWorldPosition, world) > jumpRejectDistance;
                if (jumped)
                {
                    match.consecutiveHits = 0;
                    return match;
                }
                match.worldPosition = world;
                match.worldSize = worldSize;
                match.smoothedWorldPosition = Vector3.Lerp(match.smoothedWorldPosition, world, positionSmoothing);
                // Size rides the same smoothing and the same jump gate: a bad depth batch mis-sizes
                // exactly when it mis-places, so both are rejected together.
                match.smoothedWorldSize = Vector3.Lerp(match.smoothedWorldSize, worldSize, positionSmoothing);
            }

            match.className = detection.className;
            match.confidence = Mathf.Max(match.confidence * 0.9f, detection.confidence);
            match.lastAcquiredAtRealtimeSeconds = frameTiming.isLiveCapture ? frameTiming.acquiredAtRealtimeSeconds : double.NaN;
            // Keep a useful legacy estimate without treating result arrival as a new capture. Live
            // pruning/rendering uses the unscaled timestamp directly, including through timescale changes.
            match.lastSeenTime = frameTiming.isLiveCapture
                ? now - (float)(Time.realtimeSinceStartupAsDouble - frameTiming.acquiredAtRealtimeSeconds) : now;
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
            var realtimeNow = Time.realtimeSinceStartupAsDouble;
            for (var i = _objects.Count - 1; i >= 0; i--)
            {
                if (_objects[i].AgeAt(realtimeNow, now) <= keepAliveSeconds) continue;
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

        private TrackedObject FindNearest(int classId, Vector3 world, ISet<int> observedIds)
        {
            TrackedObject best = null;
            var bestDistance = Reach(world);
            foreach (var o in _objects)
            {
                if (o.classId != classId) continue;
                if (observedIds != null && observedIds.Contains(o.id)) continue;
                var d = Vector3.Distance(o.smoothedWorldPosition, world);
                if (float.IsNaN(d) || float.IsInfinity(d) || d > bestDistance) continue;
                bestDistance = d;
                best = o;
            }
            return best;
        }

        private static bool Finite(Vector3 value)
            => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
