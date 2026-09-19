using System;
using System.Collections.Generic;
using CutOnce.Core;
using UnityEngine;

namespace CutOnce.AR
{
    public enum AlignmentState { Restoring, Placing, Locked }

    /// <summary>
    /// The only writer of AssemblyRoot's pose. No markers and no calibration: point where the build should stand,
    /// turn it with the stick, pull the trigger. The pose is then pinned with a spatial anchor and comes back on
    /// the next launch. Afterwards, hold the grip to nudge. Touching the plan's two touch points with the
    /// controller is offered as a second way in, for overlaying onto something that already exists.
    /// </summary>
    public sealed class AlignmentController : MonoBehaviour
    {
        public const float PointerReach = 6f, NudgeMetresPerSecond = 0.05f, LiftMetresPerSecond = 0.03f, YawDegreesPerSecond = 20f, PlacingYawDegreesPerSecond = 90f;
        /// <summary>Two touched points must be as far apart as the plan says, within this. A larger gap means the wrong corners were touched.</summary>
        public const float TouchBaselineTolerance = 0.03f;
        const string NudgeKey = "cutonce.alignment.nudge";
        const string LockedHint = "Hold grip + stick to nudge · hold the stick button to place again";

        IOperatorInput _input; ISurfaceRaycaster _surface; IAnchorStore _anchors; AssemblyView _assembly;
        readonly List<Vector3> _touches = new List<Vector3>();
        float _yaw; bool _yawChosen, _nudged;
        /// <summary>The current lock is for this session only (build mode's): nothing about it, not even a nudge, is saved.</summary>
        bool _sessionLock;
        /// <summary>Goes up with every lock and every "place again", so a lock still waiting for its anchor knows it has been overtaken.</summary>
        int _lockTicket;

        public AlignmentState State { get; private set; } = AlignmentState.Restoring;
        /// <summary>How the current pose was reached: restored | pointed | touch_2pt | build. Shown on the HUD and useful in the event log.</summary>
        public string Method { get; private set; } = "";
        public string Hint { get; private set; } = "Looking for the saved position…";
        public bool HasSurfaceHit { get; private set; }
        public int TouchesRecorded => _touches.Count;
        public event Action Changed;

        public void Init(AssemblyView assembly, IOperatorInput input, ISurfaceRaycaster surface, IAnchorStore anchors)
        { _assembly = assembly; _input = input; _surface = surface; _anchors = anchors; }

        async void Start()
        {
            Transform anchor = null;
            try { anchor = _anchors == null ? null : await _anchors.Restore(); }
            catch (Exception e) { Debug.LogWarning("[CutOnce] Could not restore the saved anchor: " + e.Message); }
            if (this == null) return;                                   // destroyed while waiting
            if (anchor != null && State == AlignmentState.Restoring)
            {
                transform.SetParent(anchor, false);
                RestoreNudge();
                Finish("restored");
            }
            else if (State == AlignmentState.Restoring) BeginPlacing();
        }

        public void BeginPlacing()
        {
            _lockTicket++;
            State = AlignmentState.Placing; _touches.Clear(); _yawChosen = false;
            Hint = (_assembly != null && _assembly.DisplayScale < 1f ? $"Tabletop model at {_assembly.ScaleLabel} · " : "") + "Point at where the build stands · stick turns it · trigger locks";
            Changed?.Invoke();
        }

        /// <summary>
        /// Build mode knows where the design goes (the server chose a spot beside the pile, facing the viewer), so it
        /// locks there directly. The lock is for this session only: it gets a spatial anchor of its own, but the anchor and
        /// the nudge saved for the last build are left exactly as they were, so the next launch still finds them. Grip +
        /// stick nudges still work afterwards (and are not saved either). Pointing and pulling the trigger is the way to
        /// make a placement that lasts.
        /// </summary>
        public async void LockAt(Pose worldPose, string method)
        {
            int ticket = ++_lockTicket;
            transform.SetParent(null, true);
            transform.SetPositionAndRotation(worldPose.position, worldPose.rotation);
            State = AlignmentState.Locked; Method = method; Hint = LockedHint; _nudged = false; _sessionLock = true; _touches.Clear();
            Changed?.Invoke();

            Transform anchor = null;
            try { if (_anchors != null) anchor = await _anchors.CreateForSessionAt(worldPose); }
            catch (Exception e) { Debug.LogWarning("[CutOnce] Could not make a spatial anchor for this session: " + e.Message); }
            if (this == null || anchor == null || ticket != _lockTicket) return;   // overtaken by a newer lock, or by "place again"
            transform.SetParent(anchor, true);
        }

        /// <summary>
        /// Stands whatever is drawn now on a spot, as pointing at it would (the middle of its footprint on the point, its
        /// lowest face on the surface), for this session only. Build mode uses it when another run takes over:
        /// the new run appears on the build site, where the viewer is looking.
        /// </summary>
        public void StandAt(Vector3 surfacePoint, float yawDegrees, string method) =>
            LockAt(PlacementMath.StandOn(surfacePoint, yawDegrees, LocalBounds(), _assembly.DisplayScale), method);

        void Update()
        {
            if (_input == null || _assembly == null || _assembly.Plan == null || _assembly.Plan.parts.Count == 0) return;   // nothing built: nothing to place
            if (State == AlignmentState.Placing) Place();
            else if (State == AlignmentState.Locked) NudgeOrReplace();
        }

        // ── placing ─────────────────────────────────────────────────────────────────────────────────────────────
        void Place()
        {
            if (_input.MarkDown) { RecordTouch(); return; }
            if (!_input.TryGetPointer(out var ray)) { HasSurfaceHit = false; return; }

            // The real surface under the pointer when depth sensing has one; otherwise the floor (tracking origin is floor level).
            Vector3 point = default;
            HasSurfaceHit = (_surface != null && _surface.Raycast(ray, out point)) || PlacementMath.HitHorizontalPlane(ray, 0f, PointerReach, out point);
            if (!HasSurfaceHit) return;

            if (!_yawChosen) { _yaw = PlacementMath.YawToward(point, ray.origin); _yawChosen = true; }     // first frame: face the operator
            _yaw += _input.Stick.x * PlacingYawDegreesPerSecond * Time.deltaTime;

            var pose = PlacementMath.StandOn(point, _yaw, LocalBounds(), _assembly.DisplayScale);
            transform.SetParent(null, true);
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            if (_input.TriggerDown) Lock("pointed");
        }

        void RecordTouch()
        {
            var points = _assembly.Plan.touch_points;
            if (points == null || points.Count < 2) { Hint = "This plan has no touch points · point and pull the trigger instead"; Changed?.Invoke(); return; }
            if (_assembly.DisplayScale < 1f) { Hint = $"Touch points need the plan at full size; this one is shown at {_assembly.ScaleLabel} · pull the trigger to place it"; Changed?.Invoke(); return; }

            _touches.Add(_input.TipWorld);
            if (_touches.Count == 1) { Hint = $"Now touch: {points[1].name}"; Changed?.Invoke(); return; }

            Vector3 a1 = ModelSpace.Point(points[0].position), a2 = ModelSpace.Point(points[1].position);
            var pose = AlignmentSolver.SolveTwoPoint(a1, a2, _touches[0], _touches[1], out float baseline, out _);
            _touches.Clear();
            if (baseline > TouchBaselineTolerance)
            {
                Hint = $"Those points are {baseline * 100f:0.#} cm off the plan's spacing · touch {points[0].name} again";
                Changed?.Invoke();
                return;
            }
            transform.SetParent(null, true);
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            Lock("touch_2pt");
        }

        async void Lock(string method)
        {
            int ticket = ++_lockTicket;
            State = AlignmentState.Locked; Method = method; Hint = "Saving position…"; _nudged = false; _sessionLock = false;
            PlayerPrefs.DeleteKey(NudgeKey);
            Changed?.Invoke();

            Transform anchor = null;
            try
            {
                if (_anchors != null) { await _anchors.Forget(); anchor = await _anchors.CreateAt(new Pose(transform.position, transform.rotation)); }
            }
            catch (Exception e) { Debug.LogWarning("[CutOnce] Could not save a spatial anchor: " + e.Message); }
            if (this == null || ticket != _lockTicket) return;          // overtaken while the anchor was being saved: that lock's pose is not this one's
            if (anchor != null) transform.SetParent(anchor, true);
            Finish(method);
        }

        void Finish(string method)
        {
            State = AlignmentState.Locked; Method = method;
            Hint = LockedHint;
            Changed?.Invoke();
        }

        // ── after the lock ──────────────────────────────────────────────────────────────────────────────────────
        float _replaceHeldFor;

        void NudgeOrReplace()
        {
            _replaceHeldFor = _input.StickClickHeld ? _replaceHeldFor + Time.deltaTime : 0f;
            if (_replaceHeldFor > 1f) { _replaceHeldFor = 0f; BeginPlacing(); return; }

            if (!_input.GripHeld)
            {
                if (_nudged) { if (!_sessionLock) SaveNudge(); _nudged = false; }   // a session lock's nudge is relative to tonight's anchor: saved, it would offset the desk tomorrow
                return;
            }
            Vector2 stick = _input.Stick;
            if (stick.sqrMagnitude < 0.04f) return;                    // dead zone
            var bounds = LocalBounds();
            var pivot = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z) * _assembly.DisplayScale;
            var pose = _input.TriggerHeld
                ? PlacementMath.Nudge(new Pose(transform.position, transform.rotation), new Vector3(0, stick.y * LiftMetresPerSecond * Time.deltaTime, 0), stick.x * YawDegreesPerSecond * Time.deltaTime, pivot)
                : PlacementMath.Nudge(new Pose(transform.position, transform.rotation), new Vector3(stick.x, 0, stick.y) * (NudgeMetresPerSecond * Time.deltaTime), 0f, pivot);
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            _nudged = true;
        }

        [Serializable] struct SavedNudge { public Vector3 position; public Quaternion rotation; }

        /// <summary>The nudge is the root's pose relative to its anchor, so it survives a restart together with the anchor.</summary>
        void SaveNudge()
        {
            PlayerPrefs.SetString(NudgeKey, JsonUtility.ToJson(new SavedNudge { position = transform.localPosition, rotation = transform.localRotation }));
            PlayerPrefs.Save();
        }

        void RestoreNudge()
        {
            if (!PlayerPrefs.HasKey(NudgeKey)) { transform.localPosition = Vector3.zero; transform.localRotation = Quaternion.identity; return; }
            var saved = JsonUtility.FromJson<SavedNudge>(PlayerPrefs.GetString(NudgeKey));
            transform.localPosition = saved.position;
            transform.localRotation = saved.rotation.normalized;
        }

        /// <summary>The model's bounds in AssemblyRoot's own frame (cached by AssemblyView when the plan is built).</summary>
        public Bounds LocalBounds() => _assembly.LocalBounds;
    }
}
