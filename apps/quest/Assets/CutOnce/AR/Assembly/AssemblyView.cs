using System.Collections.Generic;
using CutOnce.Core;
using UnityEngine;

namespace CutOnce.AR
{
    /// <summary>
    /// The hologram: one PartView per plan part under this transform, which is AssemblyRoot. This transform's pose
    /// is the alignment and only the alignment code moves it. Looks are pushed in from outside (Show), so this class
    /// never decides what state a part is in.
    /// </summary>
    public sealed class AssemblyView : MonoBehaviour
    {
        readonly Dictionary<string, PartView> _views = new Dictionary<string, PartView>();
        readonly Dictionary<Collider, PartView> _byCollider = new Dictionary<Collider, PartView>();
        Material _material;

        public PlanDto Plan { get; private set; }
        public IReadOnlyDictionary<string, PartView> Views => _views;
        /// <summary>The model's bounds in this transform's own frame (model metres). Worked out once per Build, because placement reads it every frame.</summary>
        public Bounds LocalBounds { get; private set; }

        /// <summary>
        /// 1 for anything you build at full size. A building is shown as a tabletop model at the largest architectural
        /// scale that keeps it under <see cref="TabletopMaxMetres"/> long (a 91 m building becomes 1:200 and 46 cm).
        /// </summary>
        public float DisplayScale { get; private set; } = 1f;
        /// <summary>"1:200", or empty at full size.</summary>
        public string ScaleLabel => DisplayScale >= 1f ? "" : $"1:{Mathf.RoundToInt(1f / DisplayScale)}";

        public const float FullSizeUpToMetres = 4f, TabletopMaxMetres = 0.8f;
        static readonly int[] ArchitecturalScales = { 20, 50, 100, 200, 500, 1000, 2000 };
        /// <summary>In the room, whatever the scale: lines about 1.2 mm wide, dashes 3 cm, colliders padded 1 cm (4 mm at tabletop).</summary>
        const float LineWidthInRoom = 0.0012f, DashInRoom = 0.03f;

        public static float ScaleFor(Bounds model)
        {
            float span = Mathf.Max(model.size.x, model.size.z);
            if (span <= FullSizeUpToMetres) return 1f;
            foreach (int n in ArchitecturalScales) if (span / n <= TabletopMaxMetres) return 1f / n;
            return 1f / ArchitecturalScales[ArchitecturalScales.Length - 1];
        }

        /// <summary>
        /// Throws away the old hologram and builds the plan's parts. Parts the factory cannot draw are skipped and reported.
        /// <paramref name="models"/> holds the meshes of the plan's model files by node name; without them, model parts draw as boxes.
        /// </summary>
        public List<string> Build(PlanDto plan, IReadOnlyDictionary<string, GlbMesh> models = null)
        {
            foreach (var view in _views.Values) if (view != null) Objects.Discard(view.gameObject);
            _views.Clear(); _byCollider.Clear();
            Plan = plan;
            if (_material == null) _material = HologramMaterial.Create();

            // First the shapes, so the model's size (and so its display scale) is known before anything scale-dependent is made.
            var skipped = new List<string>();
            var shapes = new List<(PartDto part, ShapeFactory.Built built)>();
            var bounds = new Bounds(); bool any = false;
            foreach (var part in plan.parts)
            {
                var built = ShapeFactory.Build(part, models);
                if (built?.Mesh == null) { skipped.Add(part.part_id); continue; }
                shapes.Add((part, built));
                Grow(ref bounds, ref any, built.LocalPosition, built.LocalRotation, built.Mesh.bounds);
            }
            LocalBounds = bounds;
            DisplayScale = ScaleFor(bounds);
            transform.localScale = Vector3.one * DisplayScale;

            float inModel = 1f / DisplayScale;
            float padding = (DisplayScale < 1f ? 0.004f : PartView.ColliderPadding) * inModel;
            float gridStep = DisplayScale < 1f ? 1.5f : 0.1f;               // a curtain-wall bay for a building, 10 cm for furniture
            foreach (var (part, built) in shapes)
            {
                if (built.Source != null) built.EdgeLines = GlbMeshes.EdgeLines(built.Source, LineWidthInRoom * inModel);
                var view = PartView.Create(part, built, transform, _material, true, padding, gridStep, DashInRoom * inModel);
                _views[part.part_id] = view;
                _byCollider[view.GetComponent<Collider>()] = view;
            }
            return skipped;
        }

        /// <summary>Pushes one look to every part. Estimated parts (accuracy tags) draw dashed whatever their state.</summary>
        public void Show(IReadOnlyDictionary<string, PartVisual> visuals, HologramPalette palette)
        {
            foreach (var pair in _views)
            {
                if (!visuals.TryGetValue(pair.Key, out var visual)) continue;
                var style = palette.StyleFor(visual);
                style.dashed = pair.Value.Accuracy.IsApproximate;
                pair.Value.Apply(style);
            }
        }

        /// <summary>The part a physics hit landed on, without a GetComponent in the frame loop. Null for anything that is not a part.</summary>
        public PartView ViewOf(Collider collider) => collider != null && _byCollider.TryGetValue(collider, out var view) ? view : null;

        static void Grow(ref Bounds total, ref bool any, Vector3 position, Quaternion rotation, Bounds mesh)
        {
            for (int i = 0; i < 8; i++)
            {
                var corner = mesh.center + Vector3.Scale(mesh.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = position + rotation * corner;
                if (!any) { total = new Bounds(p, Vector3.zero); any = true; } else total.Encapsulate(p);
            }
        }

        public PartView ViewOf(string partId) => partId != null && _views.TryGetValue(partId, out var view) ? view : null;
    }
}
