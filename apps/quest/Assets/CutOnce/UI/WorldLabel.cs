using UnityEngine;
using UnityEngine.UI;

namespace CutOnce.UI
{
    /// <summary>
    /// A small floating label that always faces you: an object's name and size, or an idea's title. Built in code like
    /// the HUD (same font, same world-space canvas), with a dark outline because it floats over passthrough with no
    /// panel behind it: white text alone vanishes over a white table.
    /// </summary>
    public sealed class WorldLabel : MonoBehaviour
    {
        const float MetresPerUnit = 0.0008f;             // 18-unit text ≈ 1.4 cm tall: readable at 1.5 m
        Text _text; Transform _camera;

        public static WorldLabel Create(Transform parent, string text, Vector3 worldPosition)
        {
            var go = new GameObject("[Label]", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.position = worldPosition;
            var canvas = go.AddComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
            VrTextQuality.AddScaler(go);
            var rect = (RectTransform)go.transform; rect.sizeDelta = new Vector2(320, 64); rect.localScale = Vector3.one * MetresPerUnit;
            var label = go.AddComponent<WorldLabel>();
            var t = new GameObject("text", typeof(RectTransform)).AddComponent<Text>();
            t.rectTransform.SetParent(rect, false);
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one; t.rectTransform.offsetMin = t.rectTransform.offsetMax = Vector2.zero;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.fontSize = 18; t.alignment = TextAnchor.MiddleCenter; t.color = Color.white; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Truncate;
            t.text = text ?? "";
            var outline = t.gameObject.AddComponent<Outline>();       // same mesh and material as the text: no extra draw call
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f); outline.effectDistance = new Vector2(1f, -1f);
            label._text = t;
            return label;
        }

        public void Set(string text) => _text.text = text ?? "";

        void LateUpdate()
        {
            if (_camera == null) { var c = Camera.main; if (c == null) return; _camera = c.transform; }
            var away = transform.position - _camera.position;          // a canvas is read from its -Z side, so +Z points away from you
            if (away.sqrMagnitude > 1e-6f) transform.rotation = Quaternion.LookRotation(away);
        }
    }
}
