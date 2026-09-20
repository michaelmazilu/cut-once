using System.Collections.Generic;
using System.Globalization;
using CutOnce.AR;
using CutOnce.Core;
using CutOnce.UI;
using UnityEngine;

namespace CutOnce.Device
{
    /// <summary>
    /// Outlines and labels over the real objects build mode found: dim while unnamed, glowing once labelled. The root sits
    /// at the world origin, so a twin's plan-frame position becomes its Unity position through ModelSpace alone. Rebuilt
    /// once per inventory message (at most 40 twins), never per frame.
    /// </summary>
    public sealed class TwinOverlay : MonoBehaviour
    {
        public const int MaxShown = 40;
        readonly Dictionary<string, TwinDto> _twins = new Dictionary<string, TwinDto>();
        readonly Dictionary<string, PartView> _views = new Dictionary<string, PartView>();
        readonly Dictionary<string, BaseVisual> _baseLooks = new Dictionary<string, BaseVisual>();
        Material _material; HologramPalette _palette;
        float _highlightUntil;

        public void Init(Material material, HologramPalette palette) { _material = material; _palette = palette; }

        public void Show(InventoryDto inventory)
        {
            Clear();
            if (inventory?.twins == null) return;
            var named = _palette.StyleFor(new PartVisual { Base = BaseVisual.CURRENT_STEP });
            // Loud enough to read over real passthrough (the scan beat is a demo moment): the first
            // pass used 0.04/0.45 and the twins were nearly invisible on the headset.
            named.grid = false; named.pulseHz = 0; named.fillAlpha = 0.12f; named.edgeAlpha = 0.9f; named.edgeWidthPx = 2.5;
            var unnamed = _palette.StyleFor(new PartVisual { Base = BaseVisual.FUTURE });
            unnamed = unnamed.Clone(); unnamed.edgeAlpha = 0.55f; unnamed.fillAlpha = 0.06f;
            foreach (var t in inventory.twins)
            {
                if (_twins.Count >= MaxShown) break;
                if (t?.twin_id == null || !Drawable(t.shape) || t.position == null || t.position.Length != 3) continue;
                var part = AsPart(t);
                var built = ShapeFactory.Build(part);
                if (built?.Mesh == null) continue;                              // a shape the headset cannot draw
                _twins[t.twin_id] = t;
                var baseLook = inventory.labelled && IsNamed(t) ? BaseVisual.CURRENT_STEP : BaseVisual.FUTURE;
                var view = PartView.Create(part, built, transform, _material, pickable: false);
                _views[t.twin_id] = view; _baseLooks[t.twin_id] = baseLook;
                view.Apply(baseLook == BaseVisual.CURRENT_STEP ? named : unnamed);
                WorldLabel.Create(transform, Caption(t), ModelSpace.Point(t.position) + Vector3.up * (float)(Height(t.shape) / 2 + 0.05));
            }
        }

        public void Highlight(IEnumerable<string> twinIds, float seconds)
        {
            var lit = new HashSet<string>(twinIds ?? new string[0]);
            foreach (var pair in _views)
            {
                var visual = new PartVisual { Base = _baseLooks[pair.Key] };
                if (lit.Contains(pair.Key)) visual.Modifiers.Add(VisualModifier.HIGHLIGHTED);
                pair.Value.Apply(_palette.StyleFor(visual));
            }
            _highlightUntil = lit.Count > 0 ? Time.time + seconds : 0f;
        }

        void Update()
        {
            if (_highlightUntil <= 0f || Time.time < _highlightUntil) return;
            _highlightUntil = 0f;
            Highlight(null, 0f);
        }

        public void Clear()
        {
            _twins.Clear();
            _views.Clear(); _baseLooks.Clear(); _highlightUntil = 0f;
            DestroyChildren(transform);
        }

        /// <summary>Destroys everything under a build-mode root, and the meshes made for it (a generated mesh outlives its object otherwise).</summary>
        public static void DestroyChildren(Transform root)
        {
            foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true)) if (filter.sharedMesh != null) Destroy(filter.sharedMesh);
            for (int i = root.childCount - 1; i >= 0; i--) Destroy(root.GetChild(i).gameObject);
        }

        public bool TryGetWorldPose(string twinId, out Vector3 position, out Quaternion rotation)
        {
            position = default; rotation = Quaternion.identity;
            if (twinId == null || !_twins.TryGetValue(twinId, out var t)) return false;
            position = ModelSpace.Point(t.position);
            rotation = TwinPose.Rotation(t.yaw_deg);
            return true;
        }

        /// <summary>Where a design part has to start so that it lies exactly on its real object (TwinPose.StartRotation). The flight undoes the turn on the way.</summary>
        public bool TryGetStartPose(string twinId, PartDto part, out Vector3 position, out Quaternion rotation)
        {
            if (!TryGetWorldPose(twinId, out position, out rotation)) return false;
            var twin = _twins[twinId];
            rotation = TwinPose.StartRotation(twin.yaw_deg, twin.shape, part?.shape);
            return true;
        }

        /// <summary>The middle of the named objects (where the previews float), else of all of them. False when there are none.</summary>
        public bool TryGetCentre(out Vector3 centre)
        {
            Vector3 namedSum = Vector3.zero, sum = Vector3.zero; int named = 0, all = 0;
            foreach (var t in _twins.Values)
            {
                var p = ModelSpace.Point(t.position);
                sum += p; all++;
                if (IsNamed(t)) { namedSum += p; named++; }
            }
            centre = named > 0 ? namedSum / named : all > 0 ? sum / all : Vector3.zero;
            return all > 0;
        }

        /// <summary>A twin is a box or a cylinder (TwinShape). Anything else, or a box with no size, is skipped rather than thrown on.</summary>
        static bool Drawable(ShapeDto s) => s != null && (s.type == "box" ? s.size != null && s.size.Length == 3 : s.type == "cylinder" && s.diameter > 0 && s.length > 0);
        static bool IsNamed(TwinDto t) => !string.IsNullOrEmpty(t.name) && t.name != "unknown";

        static PartDto AsPart(TwinDto t) => new PartDto
        { part_id = "part_" + t.twin_id, name = t.label, kind = t.name, layer = "scan", shape = t.shape, position = t.position, rotation_quat = TwinPose.YawQuat(t.yaw_deg) };

        static double Height(ShapeDto s) => s.type == "box" ? s.size[1] : s.axis == "y" ? s.length : s.diameter;

        static string Caption(TwinDto t)
        {
            if (!IsNamed(t)) return "…";
            var c = CultureInfo.InvariantCulture;
            string size = t.shape.type == "cylinder"
                ? string.Format(c, "{0:0.#} × {1:0.#} cm", t.shape.diameter * 100, Height(t.shape) * 100)
                : string.Format(c, "{0:0.#} × {1:0.#} × {2:0.#} cm", t.shape.size[0] * 100, t.shape.size[2] * 100, t.shape.size[1] * 100);
            return $"{(string.IsNullOrEmpty(t.label) ? t.name : t.label)} · {(t.snapped ? "" : "≈")}{size}";
        }
    }
}
