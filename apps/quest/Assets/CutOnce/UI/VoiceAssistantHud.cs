using UnityEngine;
using UnityEngine.UI;

namespace CutOnce.UI
{
    /// <summary>
    /// Kit's always-visible, view-locked panel. The task HUD belongs beside the build, but the controls and state for
    /// the voice assistant must remain available when the operator turns away from it.
    /// </summary>
    public sealed class VoiceAssistantHud : MonoBehaviour
    {
        const float Width = 360f, MetresPerUnit = 0.00075f;
        static readonly Vector3 ViewOffset = new Vector3(0.22f, -0.17f, 0.72f);
        static readonly Color Panel = new Color(0.035f, 0.045f, 0.05f, 0.94f);
        static readonly Color Ink = new Color(0.92f, 0.97f, 1f, 1f);
        static readonly Color Dim = new Color(0.65f, 0.70f, 0.70f, 1f);
        static readonly Color Accent = new Color(0.58f, 0.82f, 0.73f, 1f);
        static readonly Color Warn = new Color(1f, 0.85f, 0.3f, 1f);

        RectTransform _panel;
        Text _hint, _activity, _answer;
        Transform _head;
        float _answerUntil;

        public static VoiceAssistantHud Create(Transform owner, string hint)
        {
            var go = new GameObject("[Voice HUD]", typeof(RectTransform));
            go.transform.SetParent(owner, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 20;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 4f;
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(Width, 80f);
            rect.localScale = Vector3.one * MetresPerUnit;

            var hud = go.AddComponent<VoiceAssistantHud>();
            hud.Build(rect, hint);
            return hud;
        }

        void Build(RectTransform root, string hint)
        {
            _panel = Box(root, "panel", 0f, 0f, Width, 80f, Panel);
            Box(root, "accent", 16f, 16f, 3f, 14f, Accent);
            var title = Label(root, "title", 28f, 13f, Width - 44f, 20f, 14, Accent);
            title.text = "KIT  -  VOICE ASSISTANT";
            _hint = Label(root, "hint", 16f, 42f, Width - 32f, 24f, 15, Ink);
            _hint.text = hint ?? "";
            _activity = Label(root, "activity", 16f, 0f, Width - 32f, 28f, 15, Accent);
            _answer = Label(root, "answer", 16f, 0f, Width - 32f, 100f, 15, Ink);
            Layout();
        }

        static RectTransform Place(GameObject go, RectTransform root, float x, float y, float w, float h)
        {
            var rect = (RectTransform)go.transform;
            rect.SetParent(root, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(w, h);
            return rect;
        }

        static RectTransform Box(RectTransform root, string name, float x, float y, float w, float h, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var image = go.AddComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            return Place(go, root, x, y, w, h);
        }

        static Text Label(RectTransform root, string name, float x, float y, float w, float h, int size, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var text = go.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.color = colour;
            text.alignment = TextAnchor.UpperLeft;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            Place(go, root, x, y, w, h);
            return text;
        }

        public void ShowActivity(string activity)
        {
            _activity.text = activity == "listening" ? "LISTENING..."
                           : activity == "thinking" ? "THINKING..."
                           : "";
            _activity.color = activity == "thinking" ? Warn : Accent;
            Layout();
        }

        public void ShowAnswer(string text)
        {
            ShowMessage(text, 25f, Ink);
        }

        public void ShowTip(string text, float seconds = 6f)
        {
            ShowMessage(text, seconds, Dim);
        }

        public void ShowNotice(string text, float seconds = 4f)
        {
            ShowMessage(text, seconds, Warn);
        }

        void ShowMessage(string text, float seconds, Color colour)
        {
            _answer.text = text ?? "";
            _answer.color = colour;
            _answerUntil = Time.time + seconds;
            Layout();
        }

        void Layout()
        {
            float y = 42f;
            Stack(_hint, ref y, 22f, 54f);
            Stack(_activity, ref y, 22f, 32f);
            Stack(_answer, ref y, 36f, 180f);
            float height = y + 10f;
            _panel.sizeDelta = new Vector2(Width, height);
            ((RectTransform)transform).sizeDelta = new Vector2(Width, height);
        }

        static void Stack(Text text, ref float y, float minHeight, float maxHeight)
        {
            bool visible = !string.IsNullOrEmpty(text.text);
            text.gameObject.SetActive(visible);
            if (!visible) return;
            float height = Mathf.Clamp(text.preferredHeight + 2f, minHeight, maxHeight);
            text.rectTransform.anchoredPosition = new Vector2(16f, -y);
            text.rectTransform.sizeDelta = new Vector2(Width - 32f, height);
            y += height + 8f;
        }

        void LateUpdate()
        {
            if (_answer.text.Length > 0 && Time.time > _answerUntil)
            {
                _answer.text = "";
                Layout();
            }
            if (_activity.text.Length > 0)
            {
                var colour = _activity.color;
                colour.a = 0.65f + 0.35f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 7f));
                _activity.color = colour;
            }

            if (_head == null)
            {
                var main = Camera.main;
                if (main == null) return;
                _head = main.transform;
            }
            // Keep this as an owned app object instead of parenting it to the camera, so destroying the app also
            // destroys the HUD. TransformPoint still gives it a true head-locked pose.
            transform.position = _head.TransformPoint(ViewOffset);
            transform.rotation = _head.rotation;
        }
    }
}
