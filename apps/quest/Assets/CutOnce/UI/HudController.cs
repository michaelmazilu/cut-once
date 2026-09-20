using System.Collections.Generic;
using CutOnce.Core;
using UnityEngine;
using UnityEngine.UI;

namespace CutOnce.UI
{
    /// <summary>
    /// A compact task panel. During a build it deliberately shows only the object's name, its progress bar and the
    /// controls; setup messages can use the fuller layout before an object exists.
    /// It is world-locked (a head-locked panel shakes on the cast) and built in code, so there is no prefab to break.
    /// Detailed narration belongs to the voice assistant rather than this at-a-glance panel.
    /// </summary>
    public sealed class HudController : MonoBehaviour
    {
        // The task HUD is world-locked and can end up well behind the build. Keep it physically larger than the
        // near, head-locked voice HUD so its smallest status text still subtends enough pixels in the eye buffer.
        const float Width = 460f, Height = 124f, BuildHeight = 142f, MetresPerUnit = 0.0013f;
        const string BuildControls = "B: mark / undo   ·   Hold B: flag wrong\nGrip + stick: move   ·   Trigger: lock   ·   Hold stick: place again";
        static readonly Color Panel = new Color(0.035f, 0.045f, 0.05f, 0.94f), Ink = new Color(0.92f, 0.97f, 1f, 1f), Dim = new Color(0.65f, 0.70f, 0.70f, 1f),
            Accent = new Color(0.58f, 0.82f, 0.73f, 1f), Warn = new Color(1f, 0.85f, 0.3f, 1f);

        Text _title, _progress, _stepTitle, _stepBody, _part, _status, _toast, _answer, _hint, _placement;
        RectTransform _bar, _barBack, _panel;
        bool _hasBuild;
        float _toastUntil, _answerUntil;

        public static HudController Create(Transform parent)
        {
            var go = new GameObject("[HUD]", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
            VrTextQuality.AddScaler(go, VrTextQuality.DistantDynamicPixelsPerUnit);
            var rect = (RectTransform)go.transform; rect.sizeDelta = new Vector2(Width, Height); rect.localScale = Vector3.one * MetresPerUnit;
            var hud = go.AddComponent<HudController>();
            hud.Build(rect);
            return hud;
        }

        void Build(RectTransform root)
        {
            _panel = Box(root, "panel", 0, 0, Width, Height, Panel);
            Box(root, "accent", 22, 20, 3, 14, Accent);
            _title = Label(root, "title", 34, 18, Width - 56, 22, 15, Ink, TextAnchor.UpperLeft);
            _title.text = "Cut Once";
            _status = Label(root, "status", 22, 44, Width - 44, 20, 12, Dim, TextAnchor.UpperLeft);
            _hint = Label(root, "hint", 22, 72, Width - 44, 40, 16, Ink, TextAnchor.UpperLeft);
            _progress = Label(root, "progress", 22, 72, Width - 44, 24, 16, Dim, TextAnchor.UpperLeft);
            _barBack = Box(root, "bar back", 22, 104, Width - 44, 2, new Color(1, 1, 1, 0.10f));
            _bar = Box(root, "bar", 22, 104, Width - 44, 2, Accent);
            _stepTitle = Label(root, "step title", 22, 122, Width - 44, 44, 19, Ink, TextAnchor.UpperLeft);
            _stepBody = Label(root, "step body", 22, 170, Width - 44, 72, 16, Dim, TextAnchor.UpperLeft);
            _part = Label(root, "part", 22, 250, Width - 44, 100, 14, Ink, TextAnchor.UpperLeft);
            _toast = Label(root, "toast", 22, 0, Width - 44, 44, 14, Warn, TextAnchor.UpperLeft);
            _answer = Label(root, "answer", 22, 0, Width - 44, 100, 16, Ink, TextAnchor.UpperLeft);
            _placement = Label(root, "placement", 22, 0, Width - 44, 96, 20, Accent, TextAnchor.UpperLeft);
            Layout();
        }

        // Layout is in panel units from the top-left corner, y down: the way a designer would sketch it.
        static RectTransform Place(GameObject go, RectTransform root, float x, float y, float w, float h)
        {
            var r = (RectTransform)go.transform; r.SetParent(root, false);
            r.anchorMin = r.anchorMax = new Vector2(0, 1); r.pivot = new Vector2(0, 1);
            r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h);
            return r;
        }

        static RectTransform Box(RectTransform root, string name, float x, float y, float w, float h, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var image = go.AddComponent<Image>(); image.color = colour; image.raycastTarget = false;
            return Place(go, root, x, y, w, h);
        }

        static Text Label(RectTransform root, string name, float x, float y, float w, float h, int size, Color colour, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size; text.color = colour; text.alignment = anchor; text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            Place(go, root, x, y, w, h);
            return text;
        }

        /// <param name="scaleLabel">"1:200" when a building is shown as a tabletop model; empty at full size.</param>
        public void ShowState(PlanDto plan, BuildStateDto state, List<MaterialLine> materials, IEnumerable<BuildEventDto> events, string scaleLabel = "")
        {
            _hasBuild = plan.parts.Count > 0;
            _title.text = _hasBuild ? plan.name : "Cut Once";
            _hint.text = _hasBuild ? BuildControls : "";
            _bar.sizeDelta = new Vector2((Width - 44) * Mathf.Clamp01(state.progress.pct / 100f), 2);
            Layout();
        }

        public void ShowPart(string card) { _part.text = card ?? ""; Layout(); }
        public void ShowPlacement(string text) { _placement.text = text ?? ""; Layout(); }
        public void ShowStatus(string connection, string alignment)
        {
            _status.text = connection ?? "";
            // Alignment owns placement state, but its transient status (for example "Looking for the saved
            // position…") must never replace this panel's compact controller legend.
            _hint.text = _hasBuild ? BuildControls : "";
            Layout();
        }
        public void ShowAnswer(string text) { _answer.text = text ?? ""; _answerUntil = Time.time + 25f; Layout(); }

        public void Toast(string text, float seconds = 3f)
        {
            _toast.text = text; _toastUntil = Time.time + seconds; Layout();
        }

        // Once a build exists, keep the panel glanceable: object, progress and controls only.
        void Layout()
        {
            if (_hasBuild)
            {
                _title.gameObject.SetActive(true);
                _title.fontSize = 22;
                SetRect(_title.rectTransform, 34, 16, Width - 56, 32);

                _barBack.gameObject.SetActive(true);
                _bar.gameObject.SetActive(true);
                SetRect(_barBack, 22, 61, Width - 44, 8);
                SetRect(_bar, 22, 61, _bar.sizeDelta.x, 8);

                _hint.gameObject.SetActive(true);
                SetRect(_hint.rectTransform, 22, 83, Width - 44, 46);

                _status.gameObject.SetActive(false);
                _progress.gameObject.SetActive(false);
                _stepTitle.gameObject.SetActive(false);
                _stepBody.gameObject.SetActive(false);
                _part.gameObject.SetActive(false);
                _answer.gameObject.SetActive(false);
                _placement.gameObject.SetActive(false);
                _toast.gameObject.SetActive(false);
                Resize(BuildHeight);
                return;
            }

            _title.gameObject.SetActive(true);
            _title.fontSize = 15;
            SetRect(_title.rectTransform, 34, 18, Width - 56, 22);
            _status.gameObject.SetActive(true);
            float y = 72;
            _progress.gameObject.SetActive(false);
            _bar.gameObject.SetActive(false);
            _barBack.gameObject.SetActive(false);
            _stepTitle.gameObject.SetActive(false);
            _stepBody.gameObject.SetActive(false);
            Stack(_hint, ref y, 22, 76);
            // The answer temporarily takes the context slot; selecting a part still updates its card underneath.
            if (!string.IsNullOrEmpty(_answer.text))
            {
                _part.gameObject.SetActive(false);
                Stack(_answer, ref y, 36, 220);
            }
            else
            {
                _answer.gameObject.SetActive(false);
                Stack(_part, ref y, 30, 112);
            }
            Stack(_placement, ref y, 28, 104);
            Stack(_toast, ref y, 22, 64);
            Resize(y + 14);
        }

        static void SetRect(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
        }

        void Resize(float height)
        {
            _panel.sizeDelta = new Vector2(Width, height);
            ((RectTransform)transform).sizeDelta = new Vector2(Width, height);
        }

        static void Stack(Text text, ref float y, float minHeight, float maxHeight)
        {
            bool visible = !string.IsNullOrEmpty(text.text);
            text.gameObject.SetActive(visible);
            if (!visible) return;
            float height = Mathf.Clamp(text.preferredHeight + 2, minHeight, maxHeight);
            text.rectTransform.anchoredPosition = new Vector2(22, -y);
            text.rectTransform.sizeDelta = new Vector2(Width - 44, height);
            y += height + 10;
        }

        void Update()
        {
            bool changed = false;
            if (_toast.text.Length > 0 && Time.time > _toastUntil) { _toast.text = ""; changed = true; }
            if (_answer.text.Length > 0 && Time.time > _answerUntil) { _answer.text = ""; changed = true; }
            if (changed) Layout();
        }

        /// <summary>Before anything is placed the panel carries the instructions, so it floats in front of the operator, a little below eye level.</summary>
        public void StandInFrontOf(Vector3 head, Vector3 forward)
        {
            forward.y = 0f;
            forward = forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized;
            ((RectTransform)transform).pivot = new Vector2(0.5f, 0.5f);
            transform.position = head + forward * 1.2f + Vector3.down * 0.35f;
            transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }

        /// <summary>Stands the panel behind the build as seen from the viewer, tilted up toward them. Called at lock time, then left alone.</summary>
        public void StandBehind(Bounds build, Vector3 viewer)
        {
            Vector3 away = build.center - viewer; away.y = 0f;
            away = away.sqrMagnitude < 1e-4f ? Vector3.forward : away.normalized;
            float reach = Mathf.Max(build.extents.x, build.extents.z) + 0.25f;
            var rect = (RectTransform)transform;
            rect.pivot = new Vector2(0.5f, 0f);
            transform.position = new Vector3(build.center.x, build.max.y + 0.12f, build.center.z) + away * reach;
            // A canvas is read from its -Z side. Pitching its forward (+Z) down by 12 degrees turns its face up toward standing eyes.
            transform.rotation = Quaternion.LookRotation(away, Vector3.up) * Quaternion.Euler(12f, 0f, 0f);
        }
    }
}
