using System.Collections.Generic;
using CutOnce.Core;
using UnityEngine;
using UnityEngine.UI;

namespace CutOnce.UI
{
    /// <summary>
    /// The panel that stands behind the build: progress, the current step with its instruction and materials, what
    /// is still to use, the part under the pointer, recent history, connection and alignment status, and toasts.
    /// It is world-locked (a head-locked panel shakes on the cast) and built in code, so there is no prefab to break.
    /// All wording comes from Core.HudText.
    /// </summary>
    public sealed class HudController : MonoBehaviour
    {
        const float Width = 640f, Height = 420f, MetresPerUnit = 0.001f;
        static readonly Color Panel = new Color(0.04f, 0.07f, 0.10f, 0.78f), Ink = new Color(0.92f, 0.97f, 1f, 1f), Dim = new Color(0.62f, 0.75f, 0.82f, 1f),
            Accent = new Color(0.13f, 0.83f, 0.93f, 1f), Warn = new Color(1f, 0.85f, 0.3f, 1f);

        Text _title, _progress, _stepTitle, _stepBody, _materials, _part, _history, _status, _toast, _answer, _copilot;
        RectTransform _bar;
        float _toastUntil;

        public static HudController Create(Transform parent)
        {
            var go = new GameObject("[HUD]", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 4f;                  // crisp text at arm's length
            var rect = (RectTransform)go.transform; rect.sizeDelta = new Vector2(Width, Height); rect.localScale = Vector3.one * MetresPerUnit;
            var hud = go.AddComponent<HudController>();
            hud.Build(rect);
            return hud;
        }

        void Build(RectTransform root)
        {
            Box(root, "panel", 0, 0, Width, Height, Panel);
            _title = Label(root, "title", 20, 14, 400, 28, 20, Dim, TextAnchor.UpperLeft);
            _status = Label(root, "status", 320, 14, 300, 28, 16, Dim, TextAnchor.UpperRight);
            _progress = Label(root, "progress", 20, 44, 400, 40, 30, Ink, TextAnchor.UpperLeft);
            Box(root, "bar back", 20, 92, Width - 40, 8, new Color(1, 1, 1, 0.12f));
            _bar = Box(root, "bar", 20, 92, Width - 40, 8, Accent);
            _stepTitle = Label(root, "step title", 20, 112, Width - 40, 28, 21, Accent, TextAnchor.UpperLeft);
            _stepBody = Label(root, "step body", 20, 142, Width - 40, 78, 17, Ink, TextAnchor.UpperLeft);
            _materials = Label(root, "materials", 20, 228, 290, 130, 15, Dim, TextAnchor.UpperLeft);
            _part = Label(root, "part", 330, 228, 290, 130, 15, Ink, TextAnchor.UpperLeft);
            _history = Label(root, "history", 20, 362, Width - 40, 50, 12, Dim, TextAnchor.LowerLeft);
            _toast = Label(root, "toast", 20, -44, Width - 40, 36, 20, Warn, TextAnchor.MiddleCenter);
            _copilot = Label(root, "copilot activity", 20, -82, Width - 40, 36, 22, Accent, TextAnchor.MiddleCenter);
            _answer = Label(root, "answer", 20, Height + 8, Width - 40, 90, 17, Ink, TextAnchor.UpperLeft);
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
            _title.text = string.IsNullOrEmpty(scaleLabel) ? $"{plan.name} · revision {plan.revision}" : $"{plan.name} · revision {plan.revision} · {scaleLabel}";
            _progress.text = $"{HudText.Progress(state)}   <size=16>{HudText.TimeLeft(state)}</size>";
            _bar.sizeDelta = new Vector2((Width - 40) * Mathf.Clamp01(state.progress.pct / 100f), 8);
            _stepTitle.text = HudText.StepTitle(plan, state);
            _stepBody.text = HudText.StepBody(plan, state);
            _materials.text = HudText.Materials(materials);
            _history.text = string.Join("\n", HudText.History(plan, events, state));
        }

        public void ShowPart(string card) => _part.text = card ?? "";
        public void ShowStatus(string connection, string alignment) => _status.text = string.IsNullOrEmpty(alignment) ? connection : $"{connection} · {alignment}";
        public void ShowAnswer(string text) => _answer.text = text ?? "";

        public void ShowCopilotActivity(string activity)
        {
            _copilot.text = activity == "listening" ? "LISTENING  -  release A to send"
                          : activity == "thinking" ? "THINKING..."
                          : "";
            _copilot.color = activity == "thinking" ? Warn : Accent;
        }

        public void Toast(string text, float seconds = 3f) { _toast.text = text; _toastUntil = Time.time + seconds; }

        void Update()
        {
            if (_toast.text.Length > 0 && Time.time > _toastUntil) _toast.text = "";
            if (_copilot.text.Length > 0)
            {
                var c = _copilot.color;
                c.a = 0.65f + 0.35f * (0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 7f));
                _copilot.color = c;
            }
        }

        /// <summary>Before anything is placed the panel carries the instructions, so it floats in front of the operator, a little below eye level.</summary>
        public void StandInFrontOf(Vector3 head, Vector3 forward)
        {
            forward.y = 0f;
            forward = forward.sqrMagnitude < 1e-4f ? Vector3.forward : forward.normalized;
            ((RectTransform)transform).pivot = new Vector2(0.5f, 0.5f);
            transform.position = head + forward * 1.1f + Vector3.down * 0.25f;
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
