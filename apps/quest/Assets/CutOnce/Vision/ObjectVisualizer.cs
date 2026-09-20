using Meta.XR.EnvironmentDepth;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// The smart-glasses look: the room is just the room, and a recognised object wears a subtle
    /// blue highlight with a small name above it. Nothing else is drawn.
    ///
    /// The highlight is the app's own hologram shader (thin pixel-width edges, faint fill — the
    /// same look as Kit's designs), NOT RoomSense's grid material: grids read as "debug view", and
    /// this must read as "your glasses recognised the bottle". Two intensities:
    ///   - recognised: corner brackets only, barely-there fill — present, never loud;
    ///   - focused (the one being looked at): full thin edges, slightly stronger fill.
    /// The box is scaled to the tracked object's SMOOTHED estimated size (from the detection box
    /// and the depth samples), so a bottle gets a bottle-sized highlight, not a default cube.
    ///
    /// Everything follows the tracked object's smoothed pose, never the raw per-frame measurement,
    /// which is the difference between a label that sits on the bottle and one that vibrates.
    /// In VisionDebug mode the label gains the numbers (confidence, id, size); nothing else changes.
    /// </summary>
    public class ObjectVisualizer : MonoBehaviour
    {
        // Tuned against the palette's own over-passthrough values (MISSING: edge 0.9; CURRENT_STEP:
        // fill 0.35): anything much quieter than these reads as "nothing there" on a real headset,
        // which is exactly what happened to the first draft of this file at edge 0.5 / fill 0.04.
        [Header("Recognised (every visible object)")]
        public Color edgeColour = new Color(0.13f, 0.83f, 0.93f);   // #22D3EE, the app's cyan
        [Range(0f, 1f)] public float edgeAlpha = 0.95f;
        [Range(0f, 1f)] public float fillAlpha = 0.14f;
        public float edgeWidthPx = 2.5f;

        [Header("Focused (the one being looked at)")]
        public Color focusEdgeColour = new Color(0.40f, 0.91f, 0.98f); // #67E8F9, brighter cyan
        [Range(0f, 1f)] public float focusEdgeAlpha = 1f;
        [Range(0f, 1f)] public float focusFillAlpha = 0.28f;
        public float focusEdgeWidthPx = 3.5f;
        [Tooltip("The looked-at object breathes. 0 stops it.")]
        public float focusPulseHz = 1.2f;

        [Header("Surface paint (on device, where live depth exists)")]
        [Tooltip("Scale-up of the box for painting, so a snug detection never clips the object's silhouette.")]
        public float surfacePad = 1.2f;
        [Tooltip("How much passthrough the glow replaces. Low on purpose: the real object must stay visible under it.")]
        [Range(0f, 1f)] public float surfaceAlpha = 0.35f;
        [Range(0f, 1f)] public float focusSurfaceAlpha = 0.5f;
        [Tooltip("Metres the paint box's bottom sits above the object's bottom, so the desk it stands on is outside the box.")]
        public float surfaceLift = 0.015f;

        [Header("Label")]
        public float labelGap = 0.04f;      // metres above the top of the highlight
        public float labelSize = 0.005f;

        [Tooltip("How fast visuals catch up to the tracked position, in metres/second of lerp.")]
        public float followSpeed = 8f;

        private class Cached
        {
            public Transform transform, label;
            public MeshRenderer highlight;
            public TextMesh text, shadow;
            public string lastName;
            public bool lastFocused, lastDebug, surfaceOn;
            public float nextDebugRefresh;
        }
        private readonly System.Collections.Generic.Dictionary<GameObject, Cached> _visuals = new();
        private Transform _camera;
        private Material _material;
        private Material _surface;      // null = no live depth here; the box look is used instead
        private EnvironmentDepthManager _depth;
        private MaterialPropertyBlock _props;

        private static readonly int FillColor = Shader.PropertyToID("_FillColor");
        private static readonly int EdgeColor = Shader.PropertyToID("_EdgeColor");
        private static readonly int EdgeWidthPxId = Shader.PropertyToID("_EdgeWidthPx");
        private static readonly int Brackets = Shader.PropertyToID("_Brackets");
        private static readonly int Grid = Shader.PropertyToID("_Grid");
        private static readonly int PulseHz = Shader.PropertyToID("_PulseHz");
        private static readonly int EdgeMode = Shader.PropertyToID("_EdgeMode");
        private static readonly int HalfSize = Shader.PropertyToID("_HalfSize");
        private static readonly int RevealY = Shader.PropertyToID("_RevealY");
        private static readonly int TintId = Shader.PropertyToID("_Tint");

        private void Awake()
        {
            _props = new MaterialPropertyBlock();
            // The hologram shader lives in Resources, so it survives build stripping; loading it here
            // instead of referencing CutOnce.AR keeps the Vision assembly free-standing.
            var shader = Resources.Load<Shader>("CutOnce/Hologram");
            if (shader == null) shader = Shader.Find("CutOnce/Hologram");
            if (shader == null)
            {
                Debug.LogError("[Vision] hologram shader missing; highlights will use a plain unlit look.");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            _material = new Material(shader) { name = "Vision highlight (shared)" };

            // The segmentation look needs the live depth texture, which exists on the headset and not
            // over Link or in the Editor. Where it exists: one depth manager (reused if the scene has
            // one), soft mode so silhouette edges feather, and the paint shader instead of box edges.
            if (EnvironmentDepthManager.IsSupported)
            {
                var depth = FindAnyObjectByType<EnvironmentDepthManager>();
                if (depth == null) depth = gameObject.AddComponent<EnvironmentDepthManager>();
                depth.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
                _depth = depth;
                var paint = Resources.Load<Shader>("SurfaceGlow");
                if (paint != null) _surface = new Material(paint) { name = "Vision surface paint (shared)" };
                else Debug.LogError("[Vision] SurfaceGlow shader missing from Resources; using the box look.");
            }
        }

        /// <summary>Create or update the visual for a tracked object. Focused = the one under the gaze.</summary>
        public void Show(TrackedObject o, bool focused = false)
        {
            if (o.visual == null) o.visual = Build(o);
            if (!o.visual.activeSelf) o.visual.SetActive(true);
            if (!_visuals.TryGetValue(o.visual, out var cached)) return;

            var t = o.visual.transform;
            // Lerp on top of the EMA: the EMA settles the measurement, this settles the rendering.
            // The size goes on the CUBE, never the root: the label billboards, and a rotated child
            // under a non-uniform scale shears its glyphs.
            var follow = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
            t.position = Vector3.Lerp(t.position, o.DisplayCentre, follow);
            var cube = cached.highlight.transform;
            // Depth is supported but arrives late (first seconds, after sleep). Until it does, and
            // whenever it drops, this object wears the box look; the moment it is back, the paint.
            var paintNow = _surface != null && _depth != null && _depth.IsDepthAvailable;
            var targetSize = paintNow ? o.DisplaySize * surfacePad : o.DisplaySize;
            cube.localScale = Vector3.Lerp(cube.localScale, targetSize, follow);
            // The turn goes on the cube for the same reason the size does: the label is a sibling and must stay
            // upright and facing the viewer. A box on a desk is turned, never tipped, so yaw is the only axis.
            cube.localRotation = Quaternion.Slerp(cube.localRotation, Quaternion.Euler(0f, o.DisplayYawDeg, 0f), follow);
            // The padding grows sideways and upward only, and the bottom is lifted a touch: the desk a
            // bottle stands on must be outside the box, or the paint draws a slab of desk under it. The lift is
            // along the ROOT's up, which the yaw above never tilts, so a turned box still sits on the desk.
            var lift = paintNow ? surfaceLift : 0f;
            cube.localPosition = new Vector3(0f, (cube.localScale.y - o.DisplaySize.y) * 0.5f + lift, 0f);

            if (cached.lastFocused != focused || cached.surfaceOn != paintNow)
            {
                cached.lastFocused = focused;
                cached.surfaceOn = paintNow;
                cached.highlight.sharedMaterial = paintNow ? _surface : _material;
                Style(cached.highlight, focused, paintNow);
            }

            // Rebuilt only when something changed (rule 10: no strings every frame). In debug mode the
            // numbers refresh twice a second, which is plenty for reading and free the rest of the time.
            var refreshLabel = cached.lastName != o.className
                || cached.lastDebug != VisionDebug.Enabled
                || (VisionDebug.Enabled && Time.time >= cached.nextDebugRefresh);
            if (cached.text != null && refreshLabel)
            {
                cached.lastName = o.className;
                cached.lastDebug = VisionDebug.Enabled;
                cached.nextDebugRefresh = Time.time + 0.5f;
                var wanted = LabelText(o);
                cached.text.text = wanted;
                if (cached.shadow != null) cached.shadow.text = wanted;
            }

            // The label sits just above the highlight's top face, whatever size the object is.
            cached.label.localPosition = new Vector3(0f, cube.localPosition.y + cube.localScale.y * 0.5f + labelGap, 0f);

            // Billboard: face the headset, upright, so text is never mirrored or tilted.
            if (_camera == null && Camera.main != null) _camera = Camera.main.transform;
            if (_camera != null)
            {
                var away = cached.label.position - _camera.position;
                away.y = 0f;
                if (away.sqrMagnitude > 0.0001f) cached.label.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }

        private string LabelText(TrackedObject o)
        {
            var name = string.IsNullOrEmpty(o.className) ? "object" : o.className.ToUpperInvariant();
            if (!VisionDebug.Enabled) return name;
            var s = o.DisplaySize;
            // Two confidences, deliberately separate: how sure the model is that it is a bottle, and how sure the
            // GEOMETRY is. A crisp box around a thing the model has misnamed should not read as certainty.
            var geometry = o.hasMeasuredBox ? $"g{o.geometryConfidence * 100f:0}%" : "unmeasured";
            return $"{name} · {o.confidence * 100f:0}% · {geometry} · #{o.id} · " +
                   $"{s.x * 100f:0}×{s.y * 100f:0}×{s.z * 100f:0} cm · {o.DisplayYawDeg:0}°";
        }

        public void Hide(TrackedObject o)
        {
            if (o.visual != null && o.visual.activeSelf) o.visual.SetActive(false);
        }

        public void Release(TrackedObject o)
        {
            if (o.visual == null) return;
            _visuals.Remove(o.visual);
            Destroy(o.visual);
            o.visual = null;
        }

        private void Style(MeshRenderer renderer, bool focused, bool surface)
        {
            if (renderer == null) return;
            if (surface)
            {
                // Surface mode: the paint IS the highlight. No edges, no fill, no grid — the object's
                // own silhouette carries the colour; focus just turns the paint up.
                var tint = focused ? focusEdgeColour : edgeColour;
                renderer.GetPropertyBlock(_props);
                _props.SetColor(TintId, new Color(tint.r, tint.g, tint.b, focused ? focusSurfaceAlpha : surfaceAlpha));
                renderer.SetPropertyBlock(_props);
                return;
            }
            var edge = focused ? focusEdgeColour : edgeColour;
            var fill = focused ? focusFillAlpha : fillAlpha;
            renderer.GetPropertyBlock(_props);
            _props.SetColor(FillColor, new Color(edge.r, edge.g, edge.b, fill));
            _props.SetColor(EdgeColor, new Color(edge.r, edge.g, edge.b, focused ? focusEdgeAlpha : edgeAlpha));
            _props.SetFloat(EdgeWidthPxId, focused ? focusEdgeWidthPx : edgeWidthPx);
            // Full edges always — brackets alone disappear on small objects over passthrough. The
            // focused object also breathes. Never the grid: the grid is the debug look this class
            // exists to retire.
            _props.SetFloat(Brackets, 0f);
            _props.SetFloat(Grid, 0f);
            _props.SetFloat(PulseHz, focused ? focusPulseHz : 0f);
            _props.SetFloat(EdgeMode, 1f);                            // box edge maths
            _props.SetVector(HalfSize, new Vector4(0.5f, 0.5f, 0.5f, 0f)); // unit cube; world size comes from the transform
            _props.SetFloat(RevealY, 1e6f);                           // no reveal wipe on highlights
            renderer.SetPropertyBlock(_props);
        }

        private GameObject Build(TrackedObject o)
        {
            var root = new GameObject($"[Vision] {o.className}");
            root.transform.position = o.DisplayCentre;

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Highlight";
            box.transform.SetParent(root.transform, false);
            box.transform.localScale = o.DisplaySize;
            box.transform.localRotation = Quaternion.Euler(0f, o.DisplayYawDeg, 0f);
            // An overlay must never eat a controller ray or a physics query. Destroy() is deferred to
            // end of frame, so disable first — otherwise the collider is live for one frame.
            var collider = box.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Destroy(collider); }
            var renderer = box.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;   // Show() swaps to the paint once depth is available
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(root.transform, false);

            var text = labelGo.AddComponent<TextMesh>();
            text.text = "";
            text.characterSize = labelSize;
            text.fontSize = 48;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.richText = false;
            text.color = new Color(0.94f, 0.96f, 0.95f, 1f);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                text.font = font;
                labelGo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }
            // A dark text silhouette keeps small labels readable on both white walls and dark furniture.
            var shadowGo = new GameObject("Label contrast");
            shadowGo.transform.SetParent(labelGo.transform, false);
            shadowGo.transform.localPosition = new Vector3(0.0007f, -0.0007f, 0.0003f);
            var shadow = shadowGo.AddComponent<TextMesh>();
            shadow.font = text.font; shadow.fontSize = text.fontSize;
            shadow.characterSize = text.characterSize; shadow.anchor = text.anchor;
            shadow.alignment = text.alignment; shadow.richText = false;
            shadow.color = new Color(0.02f, 0.03f, 0.03f, 1f);
            shadowGo.GetComponent<MeshRenderer>().sharedMaterial = text.GetComponent<MeshRenderer>().sharedMaterial;

            var cached = new Cached { transform = root.transform, label = labelGo.transform, highlight = renderer, text = text, shadow = shadow };
            _visuals[root] = cached;
            Style(renderer, focused: false, surface: false);
            return root;
        }

        private void OnDestroy()
        {
            foreach (var pair in _visuals) if (pair.Key != null) Destroy(pair.Key);
            _visuals.Clear();
            if (_material != null) Destroy(_material);
            if (_surface != null) Destroy(_surface);
        }
    }
}
