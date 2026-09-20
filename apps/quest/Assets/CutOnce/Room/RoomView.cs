using System.Collections.Generic;
using CutOnce.AR;
using CutOnce.UI;
using UnityEngine;

namespace CutOnce.Room
{
    /// <summary>Bounded renderers, shared stereo-safe material, physical line widths and a world-space HUD.</summary>
    public sealed class RoomView : MonoBehaviour
    {
        public static readonly Color Cyan = new Color(.60f, .82f, .74f, .85f), Amber = new Color(.95f, .76f, .43f, .85f);
        Material _material;
        readonly List<GameObject> _map = new List<GameObject>();
        readonly List<LineRenderer> _drawings = new List<LineRenderer>();
        MaterialPropertyBlock _colour;                                  // made in Awake: Unity forbids it in a field initializer
        Transform _frame, _panel;
        UnityEngine.UI.Text _text;
        LineRenderer _pointer, _preview;
        public string DisplayedText => _text != null ? _text.text : "";

        void Awake()
        {
            _colour = new MaterialPropertyBlock();
            _material = HologramMaterial.Create();
            _material.SetFloat("_EdgeMode", 3);
            _material.SetColor("_FillColor", Color.clear);
            _pointer = Line("Pointer", transform, Amber, .001f);
            _pointer.useWorldSpace = true;
            _preview = Line("Draft", transform, Amber, .002f);
            _preview.useWorldSpace = true;
            var panel = new GameObject("Room instructions", typeof(RectTransform), typeof(Canvas));
            _panel = panel.transform; _panel.SetParent(transform, false);
            panel.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            VrTextQuality.AddScaler(panel);
            var rect = (RectTransform)_panel; rect.sizeDelta = new Vector2(480, 330); rect.localScale = Vector3.one * .001f;
            var image = panel.AddComponent<UnityEngine.UI.Image>(); image.color = new Color(.035f,.045f,.05f,.94f); image.raycastTarget = false;
            _text = Label("Instructions", _panel, new Vector2(436, 290), 16);
        }

        UnityEngine.UI.Text Label(string name, Transform parent, Vector2 size, int fontSize)
        {
            // Matches the project's existing HUD font; no TMP resource import or per-frame dynamic atlas rebuild.
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform; rect.sizeDelta = size;
            var text = go.AddComponent<UnityEngine.UI.Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize; text.color = Color.white; text.alignment = TextAnchor.MiddleLeft;
            text.raycastTarget = false; text.supportRichText = true;
            return text;
        }

        LineRenderer Line(string name, Transform parent, Color colour, float width)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = _material; line.useWorldSpace = false;
            line.widthMultiplier = width; line.positionCount = 0;
            line.numCapVertices = 2; line.numCornerVertices = 2;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; line.receiveShadows = false;
            _colour.SetColor("_EdgeColor", colour); line.SetPropertyBlock(_colour);
            return line;
        }

        public void PlacePanel(Transform head)
        {
            if (head == null) return;
            Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < .01f) forward = Vector3.forward;
            _panel.position = head.position + forward * 1.2f + Vector3.down * .35f;
            _panel.rotation = Quaternion.LookRotation(forward);
        }

        public void ShowMap(RoomGeometry room, Transform frame)
        {
            Clear(); _frame = frame;
            // Cap map renderers; all measured walls still remain in the geometry document.
            for (int i = 0; i < room.surfaces.Length && i < 40; i++)
            {
                var surface = room.surfaces[i];
                var line = Line(surface.label, frame, new Color(.65f,.75f,.72f,.25f), .001f);
                line.loop = true; line.positionCount = surface.outline.Length; line.SetPositions(surface.outline);
                _map.Add(line.gameObject);
            }
            var boundary = Line("Room floor boundary (not Guardian)", frame, Amber, .002f);
            boundary.loop = true; boundary.positionCount = room.floor.Length; boundary.SetPositions(room.floor); _map.Add(boundary.gameObject);
            for (int i = 0; i < room.floor.Length && i < 8; i++)
            {
                var a = room.floor[i]; var b = room.floor[(i + 1) % room.floor.Length];
                var go = new GameObject("Wall dimension", typeof(RectTransform), typeof(Canvas));
                go.transform.SetParent(frame, false); go.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
                VrTextQuality.AddScaler(go);
                var rect = (RectTransform)go.transform; rect.sizeDelta = new Vector2(220, 42); rect.localScale = Vector3.one * .001f;
                rect.localPosition = (a + b) * .5f + Vector3.up * .15f;
                Vector3 inward = -new Vector3(rect.localPosition.x, 0, rect.localPosition.z);
                if (inward.sqrMagnitude < .01f) inward = Vector3.forward;
                rect.localRotation = Quaternion.LookRotation(-inward);
                var label = Label("Metres", go.transform, new Vector2(220,42), 16);
                label.alignment = TextAnchor.MiddleCenter; label.text = Vector3.Distance(a,b).ToString("F2") + " m";
                _map.Add(go);
            }
        }
        public void SetVisible(bool visible)
        {
            _panel.gameObject.SetActive(visible);
            foreach (var go in _map) if (go != null) go.SetActive(visible);
            foreach (var line in _drawings) if (line != null) line.gameObject.SetActive(visible);
            if (!visible) { _pointer.positionCount = 0; _preview.positionCount = 0; }
        }
        public void ShowText(string text) { if (_text.text != text) _text.text = text; }
        public void Pointer(Vector3 origin, Vector3 point, bool valid)
        {
            _pointer.positionCount = 2; _pointer.SetPosition(0, origin); _pointer.SetPosition(1, point);
            _colour.SetColor("_EdgeColor", valid ? Cyan : Amber); _pointer.SetPropertyBlock(_colour);
        }
        public void HidePointer() => _pointer.positionCount = 0;
        public void Preview(Vector3[] points, int count, Transform frame)
        {
            _preview.positionCount = count;
            for (int i = 0; i < count; i++) _preview.SetPosition(i, frame.TransformPoint(points[i]));
        }
        public void CancelPreview() { if (_preview != null) _preview.positionCount = 0; }
        public void Add(RoomDrawing drawing)
        {
            var line = Line(drawing.tool.ToString(), _frame, drawing.tool == DrawingTool.Measure ? Amber : Cyan, .002f);
            line.positionCount = drawing.points.Length; line.SetPositions(drawing.points); _drawings.Add(line);
        }
        public void Undo()
        {
            if (_drawings.Count == 0) return;
            int i = _drawings.Count - 1; Destroy(_drawings[i].gameObject); _drawings.RemoveAt(i);
        }
        public void Clear()
        {
            foreach (var go in _map) if (go != null) Destroy(go);
            foreach (var line in _drawings) if (line != null) Destroy(line.gameObject);
            _map.Clear(); _drawings.Clear(); CancelPreview();
        }
        void OnDestroy() { Clear(); if (_material != null) Destroy(_material); }
    }
}
