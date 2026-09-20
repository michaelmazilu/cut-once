using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CutOnce.Room
{
    /// <summary>One controller workflow for room scan, measurements, freehand strokes and floor-aligned boxes.</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class RoomWorkspace : MonoBehaviour
    {
        [Tooltip("Explicit 4×5 m test room on laptops. Disable to use the simulator's MRUK scene data or Link.")]
        public bool useFixtureInEditor = true;
        public bool startInWorkspace;
        public bool Active { get; private set; }
        public bool Busy { get; private set; }
        public DrawingTool Tool { get; private set; }
        public IRoomSource Source { get; private set; }
        public IRoomInput Input { get; set; }
        public int DrawingCount => _drawings.Count;
        public event Action<bool> ActiveChanged;

        RoomView _view;
        RoomDrawingStore _store;
        Transform _head;
        readonly List<RoomDrawing> _drawings = new List<RoomDrawing>();
        readonly Vector3[] _stroke = new Vector3[DrawingGeometry.MaxPoints];
        readonly Vector3[] _box = new Vector3[16];
        readonly Vector3[] _measure = new Vector3[2];
        int _count, _stage;
        Vector3 _first, _second, _target;
        float _height = .75f;
        bool _waitRelease, _focused = true, _targetValid, _hasLoaded;
        string _hint = "X: open room workspace", _summary = "", _surface = "", _lastMeasurement = "";

        void Awake()
        {
            var rig = FindAnyObjectByType<OVRCameraRig>();
            _head = rig != null ? rig.centerEyeAnchor : Camera.main != null ? Camera.main.transform : null;
            Input = Application.isEditor ? new EditorRoomInput(rig != null ? rig.trackingSpace : null, _head) : new QuestRoomInput(rig != null ? rig.trackingSpace : null);
            _view = gameObject.AddComponent<RoomView>();
            _view.SetVisible(false);
            _store = new RoomDrawingStore(Path.Combine(Application.persistentDataPath, "cutonce", "rooms"));
        }
        void Start()
        {
            StartCoroutine(RefreshHud());
            if (startInWorkspace) SetActive(true);
        }
        public void SetActive(bool active)
        {
            Active = active;
            Cancel(); _waitRelease = true;
            _view.SetVisible(active); _view.PlacePanel(_head);
            ActiveChanged?.Invoke(active);
            if (active && !_hasLoaded && !Busy) LoadRoom(false);
        }
        public async void LoadRoom(bool rescan)
        {
            if (Busy) return;
            Busy = true; Cancel(); _waitRelease = true;
            try
            {
                if (Source == null) Source = Application.isEditor && useFixtureInEditor ? new FixtureRoomSource(transform) : new QuestRoomSource(transform);
                _view.Clear(); _drawings.Clear(); _hasLoaded = false;
                if (!await Source.Load(rescan) || this == null) return;
                var room = Source.Geometry;
                var restored = await _store.Load(room);
                if (this == null) return;
                _view.ShowMap(room, Source.Frame);
                foreach (var drawing in restored) { _drawings.Add(drawing); _view.Add(drawing); }
                _hasLoaded = true;
                _summary = $"{room.Area:F2} m²  •  perimeter {room.Perimeter:F2} m  •  height {room.height:F2} m";
                _hint = "Room ready. Choose a tool with left stick click.";
                _view.SetVisible(Active); _view.PlacePanel(_head);
            }
            catch (Exception e) { if (this != null) _hint = "Room could not load: " + e.Message; Debug.LogException(e); }
            finally { Busy = false; }
        }

        void Update()
        {
            if (Input == null) return;
            if (Input.Toggle) { SetActive(!Active); return; }
            if (!Active || !_focused) return;
            if (Input.Scan && !Busy) { LoadRoom(true); return; }
            if (Busy || !_hasLoaded || Source?.Geometry == null) return;
            if (_waitRelease) { if (!Input.TriggerHeld) _waitRelease = false; return; }
            if (Input.Cycle) { Cancel(); Tool = (DrawingTool)(((int)Tool + 1) % 3); _waitRelease = true; _hint = "Tool changed."; return; }
            if (Input.Undo) { Undo(); return; }
            if (!Input.TryPose(out var ray, out var tip))
            {
                Cancel(); _waitRelease = true; _view.HidePointer(); _hint = "Right controller tracking lost. Drawing paused."; return;
            }
            bool hit = true;
            Vector3 world = tip; _surface = "Controller tip";
            if (Input.Surface) hit = Source.Raycast(ray, out world, out _surface);
            _target = Source.Frame.InverseTransformPoint(world);
            _targetValid = hit && Source.Geometry.Contains(_target);
            _view.Pointer(ray.origin, hit ? world : ray.GetPoint(2), _targetValid);
            if (!_targetValid)
            {
                if (_count > 0) FinishStroke();
                _hint = hit ? "Outside the scanned room. Move back inside its outline." : "No scanned surface under the pointer.";
                return;
            }
            // Never bridge a tracking gap or an excursion outside the room while the trigger stays held.
            if (_drawings.Count >= DrawingGeometry.MaxDrawings) { _hint = "64 objects reached. B removes the last object."; return; }
            if (Tool == DrawingTool.Freehand) ReadStroke();
            else if (Tool == DrawingTool.Measure) ReadMeasure();
            else ReadBox();
        }

        void ReadStroke()
        {
            if (Input.TriggerDown) { _count = 0; _stroke[_count++] = _target; }
            if (_count > 0 && Input.TriggerHeld && (_target - _stroke[_count - 1]).sqrMagnitude >= .000025f)
            {
                if (!Source.Geometry.ContainsSegment(_stroke[_count - 1], _target)) { FinishStroke(); _hint = "Stroke stopped at the room boundary."; return; }
                _stroke[_count++] = _target;
                if (_count == _stroke.Length) { FinishStroke(); _hint = "Stroke limit reached. Release and draw another stroke."; return; }
            }
            if (_count > 0) _view.Preview(_stroke, _count, Source.Frame);
            if (!Input.TriggerHeld && _count > 0) FinishStroke();
        }
        void FinishStroke()
        {
            if (_count >= 2)
            {
                var points = new Vector3[_count]; Array.Copy(_stroke, points, _count);
                Commit(DrawingTool.Freehand, points);
            }
            _count = 0; _view.CancelPreview(); _waitRelease = true;
        }
        void ReadMeasure()
        {
            if (_stage == 1)
            {
                _measure[0] = _first; _measure[1] = _target;
                _view.Preview(_measure, 2, Source.Frame);
            }
            if (!Input.TriggerDown) return;
            if (_stage == 0) { _first = _target; _stage = 1; _hint = "Point at the second endpoint and press trigger."; }
            else if (Vector3.Distance(_first, _target) >= .01f)
            {
                if (Commit(DrawingTool.Measure, new[] { _first, _target }))
                {
                    _lastMeasurement = $"Last measurement: {Vector3.Distance(_first, _target):F3} m";
                    Cancel();
                }
            }
        }
        void ReadBox()
        {
            if (_stage > 0)
            {
                if (_stage == 1) _second = _target;
                if (Mathf.Abs(Input.HeightAxis) > .2f) _height = Mathf.Clamp(_height + Input.HeightAxis * Time.unscaledDeltaTime * .5f, .02f, Source.Geometry.height);
                DrawingGeometry.FillBox(_first, _second, _height, _box);
                _view.Preview(_box, _box.Length, Source.Frame);
            }
            if (!Input.TriggerDown) return;
            if (_stage == 0)
            {
                _first = _target; _stage = 1;
                _hint = "Pick opposite footprint corner, then press trigger.";
            }
            else if (_stage == 1)
            {
                if (Mathf.Abs(_target.x - _first.x) < .02f || Mathf.Abs(_target.z - _first.z) < .02f) { _hint = "Box width and depth must be at least 2 cm."; return; }
                _second = _target; _stage = 2;
                _hint = "Right stick up/down sets height. Trigger finishes box.";
            }
            else
            {
                if (Commit(DrawingTool.Box, DrawingGeometry.Box(_first, _second, _height))) Cancel();
            }
        }
        bool Commit(DrawingTool tool, Vector3[] points)
        {
            if (!DrawingGeometry.Valid(Source.Geometry, points)) { _hint = "Object crosses a wall, floor or ceiling. Adjust it before finishing."; return false; }
            var drawing = new RoomDrawing { tool = tool, points = points };
            _drawings.Add(drawing); _view.Add(drawing);
            _ = _store.Save(Source.Geometry, _drawings.ToArray());
            _hint = "Saved in this room. B undoes the last object.";
            return true;
        }
        public void Undo()
        {
            if (_count > 0 || _stage > 0) { Cancel(); _waitRelease = true; _hint = "Draft cancelled."; return; }
            if (_drawings.Count == 0) return;
            _drawings.RemoveAt(_drawings.Count - 1); _view.Undo();
            _ = _store.Save(Source.Geometry, _drawings.ToArray());
            _hint = "Last object removed.";
        }
        void Cancel() { _count = 0; _stage = 0; _view?.CancelPreview(); }
        void OnApplicationFocus(bool focus)
        {
            _focused = focus;
            // Editor keyboard testing needs focus to move between simulator/editor windows.
            if (!Application.isEditor && !focus) { Cancel(); _waitRelease = true; }
            if (Application.isEditor) _focused = true;
        }
        IEnumerator RefreshHud()
        {
            var delay = new WaitForSecondsRealtime(.25f);
            while (true)
            {
                if (Active)
                {
                    string details = _summary;
                    if (_hasLoaded && _targetValid)
                    {
                        float clearance = Source.Geometry.WallClearance(_target);
                        details += $"\n{_surface} • wall clearance {clearance:F2} m";
                        if (Source.IsOccupied(Source.Frame.TransformPoint(_target))) details += " • inside scanned furniture";
                    }
                    if (Tool == DrawingTool.Box && _stage > 0) details += $"\nBox: {Mathf.Abs(_second.x-_first.x):F2} × {Mathf.Abs(_second.z-_first.z):F2} × {_height:F2} m";
                    if (Tool == DrawingTool.Measure && _stage > 0) details += $"\nDistance: {Vector3.Distance(_first,_target):F3} m";
                    else if (_lastMeasurement.Length > 0) details += "\n" + _lastMeasurement;
                    string action = Tool == DrawingTool.Freehand ? "Hold right trigger to draw." : Tool == DrawingTool.Box ? "Trigger: footprint corners, then height." : "Trigger: two measurement endpoints.";
                    _view.ShowText($"<color=#94D1BA><b>Room · {Tool}</b></color>\n" +
                        (Source?.Status ?? "Opening room…") + "\n" + details + "\n\n" + (_store.Error ?? _hint) + "\n" + action +
                        "\nGrip: surface snap · left stick click: change tool\nB: undo · Y: rescan · X: close\n<color=#D9BD88>Room outline is not your safety boundary.</color>");
                }
                yield return delay;
            }
        }
        void OnDestroy() { Source?.Dispose(); }
    }
}
