using System.Collections.Generic;
using CutOnce.AR;
using CutOnce.Core;
using CutOnce.UI;
using UnityEngine;

namespace CutOnce.Device
{
    /// <summary>
    /// Up to three quarter-scale designs floating above the pile, fronts toward you. Each has a box collider (not on a
    /// PartView, so the part pointer ignores it) for "point and pull the trigger".
    /// </summary>
    public sealed class IdeaPreviews : MonoBehaviour
    {
        public const float Scale = 0.25f, Spacing = 0.4f, Lift = 0.3f, Reach = 6f;
        public const int MaxShown = 3;
        struct Item { public string ideaId; public Transform root; public BoxCollider hit; public Bounds box; }
        readonly List<Item> _items = new List<Item>();
        string _highlighted;

        public void Show(IReadOnlyList<BuildIdeaDto> ideas, Vector3 pileCentre, Vector3 viewer, Material material, HologramPalette palette)
        {
            Clear();
            var toViewer = viewer - pileCentre; toViewer.y = 0f;
            if (toViewer.sqrMagnitude < 1e-4f) toViewer = Vector3.back;
            toViewer.Normalize();
            var right = Vector3.Cross(toViewer, Vector3.up);                     // the viewer's right in Unity's left-handed frame
            var style = palette.StyleFor(new PartVisual { Base = BaseVisual.CURRENT_STEP });
            style.grid = false; style.pulseHz = 0; style.fillAlpha = 0.08;
            var shown = new List<BuildIdeaDto>();
            foreach (var idea in ideas) if (shown.Count < MaxShown && idea?.idea_id != null && idea.plan?.parts != null) shown.Add(idea);
            for (int i = 0; i < shown.Count; i++)
            {
                var idea = shown[i];
                var root = new GameObject("[Idea] " + idea.title).transform;
                root.SetParent(transform, false);
                root.position = pileCentre + Vector3.up * Lift + right * ((i - (shown.Count - 1) / 2f) * Spacing);
                root.rotation = Quaternion.LookRotation(toViewer);                // plan +Z, the front, faces you
                root.localScale = Vector3.one * Scale;
                var bounds = new Bounds(root.position, Vector3.zero);
                foreach (var part in idea.plan.parts)
                {
                    if (part?.shape == null || part.part_id == "part_surface") continue;   // the build area is the table, not part of the design
                    var built = ShapeFactory.Build(part);
                    if (built?.Mesh == null) continue;
                    var view = PartView.Create(part, built, root, material, pickable: false);
                    view.Apply(style);
                    bounds.Encapsulate(view.WorldBounds);
                }
                var hit = new GameObject("[Idea hit] " + idea.title).AddComponent<BoxCollider>();
                hit.transform.SetParent(transform, false);
                hit.transform.position = bounds.center;
                hit.size = bounds.size + Vector3.one * 0.03f;
                WorldLabel.Create(transform, idea.title, new Vector3(bounds.center.x, bounds.max.y + 0.06f, bounds.center.z));
                _items.Add(new Item { ideaId = idea.idea_id, root = root, hit = hit, box = new Bounds(bounds.center, hit.size) });
            }
        }

        /// <summary>
        /// The idea under the pointer, or null. The previews float within arm's reach, and physics never reports a collider
        /// the ray starts inside, so a hand reaching into a preview is asked about first. Then one nearest-hit ray: nothing
        /// is allocated, so it can run every frame.
        /// </summary>
        public string Hit(Ray ray)
        {
            for (int i = 0; i < _items.Count; i++) if (_items[i].box.Contains(ray.origin)) return _items[i].ideaId;
            if (_items.Count == 0 || !Physics.Raycast(ray, out var h, Reach)) return null;
            for (int i = 0; i < _items.Count; i++) if (h.collider == _items[i].hit) return _items[i].ideaId;
            return null;
        }

        public void Highlight(string ideaId)
        {
            if (ideaId == _highlighted) return;
            _highlighted = ideaId;
            for (int i = 0; i < _items.Count; i++) _items[i].root.localScale = Vector3.one * (Scale * (_items[i].ideaId == ideaId ? 1.2f : 1f));
        }

        public void Clear()
        {
            _items.Clear(); _highlighted = null;
            TwinOverlay.DestroyChildren(transform);
        }
    }
}
