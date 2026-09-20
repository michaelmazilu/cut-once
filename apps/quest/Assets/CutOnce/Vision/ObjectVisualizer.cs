using Meta.XR.EnvironmentDepth;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// The smart-glasses look: the room is just the room, and a recognised object wears a subtle
    /// blue highlight with a small name above it. Nothing else is drawn.
    ///
    /// The cube is only an invisible clipping volume: SurfaceGlow reconstructs the real surface
    /// from live environment depth and colours that surface. No depth means a name and an honest
    /// status label, never a visible cube. VisionDebug explicitly opts into diagnostic boxes.
    ///
    /// Everything follows the tracked object's smoothed pose, never the raw per-frame measurement,
    /// which is the difference between a label that sits on the bottle and one that vibrates.
    /// Old detections fade out quickly so a remembered location cannot paint an unrelated surface.
    /// </summary>
    public class ObjectVisualizer : MonoBehaviour
    {
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
        public float surfacePad = 1.1f;
        [Tooltip("How much passthrough the glow replaces. Low on purpose: the real object must stay visible under it.")]
        [Range(0f, 1f)] public float surfaceAlpha = 0.22f;
        [Range(0f, 1f)] public float focusSurfaceAlpha = 0.32f;
        [Tooltip("Metres the paint box's bottom sits above the object's bottom, so the desk it stands on is outside the box.")]
        public float surfaceLift = 0.015f;
        [Tooltip("Metres between lines on the real surface.")]
        [Min(0.01f)] public float surfaceGridSpacing = 0.06f;
        [Range(0f, 1f)] public float surfaceGridStrength = 0.22f;
        [Range(0f, 1f)] public float surfaceEdgeStrength = 0.75f;

        // The tracker keeps labels longer; only recent sightings may project colour onto depth.
        public const float SurfaceFadeStartsAfterSeconds = 0.5f;
        public const float SurfaceHiddenAfterSeconds = 0.75f;

        public static float SurfaceFreshnessAtAge(float age) => Mathf.Clamp01((SurfaceHiddenAfterSeconds - age)
            / (SurfaceHiddenAfterSeconds - SurfaceFadeStartsAfterSeconds));

        public bool HasLiveSurfaceDepth => _surface != null && _depth != null
            && _depth.isActiveAndEnabled && _depth.IsDepthAvailable;

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
            public bool lastFocused, lastDebug, surfaceOn, hasLabel;
            public float lastFreshness = -1f;
            public LabelStatus lastLabelStatus;
            public float nextDebugRefresh;
        }
        private enum LabelStatus { Live, DepthUnavailable, Reacquiring }
        private readonly System.Collections.Generic.Dictionary<GameObject, Cached> _visuals = new();
        private Transform _camera;
        private Material _material;
        private Material _surface;      // missing shader or depth always degrades to labels only
        private EnvironmentDepthManager _depth;
        private MaterialPropertyBlock _props;
        private Coroutine _providerRoutine;

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
        private static readonly int GridSpacingId = Shader.PropertyToID("_GridSpacing");
        private static readonly int GridStrengthId = Shader.PropertyToID("_GridStrength");
        private static readonly int EdgeStrengthId = Shader.PropertyToID("_EdgeStrength");
        private static readonly int MinDepthId = Shader.PropertyToID("_MinDepth");

        private void Awake()
        {
            if (_props != null) return;
            _props = new MaterialPropertyBlock();
            // The hologram shader lives in Resources, so it survives build stripping; loading it here
            // instead of referencing CutOnce.AR keeps the Vision assembly free-standing.
            var shader = Resources.Load<Shader>("CutOnce/Hologram");
            if (shader == null) shader = Shader.Find("CutOnce/Hologram");
            if (shader != null) _material = new Material(shader) { name = "Vision debug bounds (shared)" };
            else Debug.LogError("[Vision] hologram shader missing; diagnostic bounds are unavailable.");

            var paint = Resources.Load<Shader>("SurfaceGlow");
            if (paint != null)
            {
                _surface = new Material(paint) { name = "Vision surface paint (shared)" };
                _surface.SetFloat(GridSpacingId, surfaceGridSpacing);
                _surface.SetFloat(GridStrengthId, surfaceGridStrength);
                _surface.SetFloat(EdgeStrengthId, surfaceEdgeStrength);
                _surface.SetFloat(MinDepthId, 0.2f);
            }
            else Debug.LogError("[Vision] SurfaceGlow shader missing from Resources; showing labels only.");
        }

        private void OnEnable()
        {
            if (Application.isPlaying) _providerRoutine = StartCoroutine(RefreshProviders());
        }

        private void OnDisable()
        {
            if (_providerRoutine != null) StopCoroutine(_providerRoutine);
            _providerRoutine = null;
            foreach (var pair in _visuals) if (pair.Key != null) pair.Key.SetActive(false);
        }

        private System.Collections.IEnumerator RefreshProviders()
        {
            // XR can become ready after Awake. Retry without Find* in the render loop, and reuse
            // the wait object so unsupported Editor sessions do not generate garbage each second.
            var wait = new WaitForSeconds(1f);
            while (true)
            {
                if (_camera == null)
                {
                    var main = Camera.main;
                    if (main != null) _camera = main.transform;
                }
                if (_surface != null && _depth == null && EnvironmentDepthManager.IsSupported)
                {
                    _depth = FindAnyObjectByType<EnvironmentDepthManager>();
                    if (_depth == null) _depth = gameObject.AddComponent<EnvironmentDepthManager>();
                    _depth.OcclusionShadersMode = OcclusionShadersMode.SoftOcclusion;
                }
                yield return wait;
            }
        }

        /// <summary>Create or update the visual for a tracked object. Focused = the one under the gaze.</summary>
        public void Show(TrackedObject o, bool focused = false)
        {
            // Editor verification may call Show without Unity's Play-mode lifecycle.
            if (_props == null) Awake();
            if (o.visual == null) o.visual = Build(o);
            if (!o.visual.activeSelf) o.visual.SetActive(true);
            if (!_visuals.TryGetValue(o.visual, out var cached)) return;

            var t = o.visual.transform;
            // Lerp on top of the EMA: the EMA settles the measurement, this settles the rendering.
            // The size goes on the CUBE, never the root: the label billboards, and a rotated child
            // under a non-uniform scale shears its glyphs.
            var follow = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
            t.position = Vector3.Lerp(t.position, o.smoothedWorldPosition, follow);
            var cube = cached.highlight.transform;
            var debug = VisionDebug.Enabled;
            var depthReady = HasLiveSurfaceDepth;
            var age = o.AgeAt(Time.realtimeSinceStartupAsDouble, Time.time);
            var freshness = SurfaceFreshnessAtAge(age);
            var paintNow = !debug && depthReady && freshness > 0f;
            var targetSize = debug ? o.smoothedWorldSize : o.smoothedWorldSize * surfacePad;
            cube.localScale = Vector3.Lerp(cube.localScale, targetSize, follow);
            // The padding grows sideways and upward only, and the bottom is lifted a touch: the desk a
            // bottle stands on must be outside the box, or the paint draws a slab of desk under it.
            var lift = debug ? 0f : surfaceLift;
            cube.localPosition = new Vector3(0f, (cube.localScale.y - o.smoothedWorldSize.y) * 0.5f + lift, 0f);

            cached.highlight.enabled = paintNow || (debug && _material != null);
            var debugChanged = cached.lastDebug != debug;
            if (cached.lastFocused != focused || cached.surfaceOn != paintNow || debugChanged
                || cached.lastFreshness != freshness)
            {
                cached.lastFocused = focused;
                cached.surfaceOn = paintNow;
                cached.lastFreshness = freshness;
                cached.highlight.sharedMaterial = paintNow ? _surface : _material;
                Style(cached.highlight, focused, paintNow, freshness);
            }

            // Rebuilt only when something changed (rule 10: no strings every frame). In debug mode the
            // numbers refresh twice a second, which is plenty for reading and free the rest of the time.
            var labelStatus = !depthReady ? LabelStatus.DepthUnavailable
                : freshness <= 0f ? LabelStatus.Reacquiring : LabelStatus.Live;
            var refreshLabel = !cached.hasLabel || cached.lastName != o.className
                || debugChanged || cached.lastLabelStatus != labelStatus
                || (debug && Time.time >= cached.nextDebugRefresh);
            if (cached.text != null && refreshLabel)
            {
                cached.lastName = o.className;
                cached.lastDebug = debug;
                cached.lastLabelStatus = labelStatus;
                cached.hasLabel = true;
                cached.nextDebugRefresh = Time.time + 0.5f;
                var wanted = LabelText(o, debug, labelStatus);
                cached.text.text = wanted;
                if (cached.shadow != null) cached.shadow.text = wanted;
            }

            // The label sits just above the highlight's top face, whatever size the object is.
            cached.label.localPosition = new Vector3(0f, cube.localPosition.y + cube.localScale.y * 0.5f + labelGap, 0f);

            // Billboard: face the headset, upright, so text is never mirrored or tilted.
            if (_camera != null)
            {
                var away = cached.label.position - _camera.position;
                away.y = 0f;
                if (away.sqrMagnitude > 0.0001f) cached.label.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }

        private static string LabelText(TrackedObject o, bool debug, LabelStatus status)
        {
            var name = string.IsNullOrEmpty(o.className) ? "object" : o.className.ToUpperInvariant();
            if (!debug)
            {
                if (status == LabelStatus.DepthUnavailable) return name + "\nDEPTH UNAVAILABLE";
                if (status == LabelStatus.Reacquiring) return name + "\nREACQUIRING";
                return name;
            }
            var s = o.smoothedWorldSize;
            return $"{name} · {o.confidence * 100f:0}% · #{o.id} · {s.x * 100f:0}×{s.y * 100f:0}×{s.z * 100f:0} cm";
        }

        public void Hide(TrackedObject o)
        {
            if (o.visual != null && o.visual.activeSelf) o.visual.SetActive(false);
        }

        public void Release(TrackedObject o)
        {
            if (o.visual == null) return;
            _visuals.Remove(o.visual);
            DestroyOwned(o.visual);
            o.visual = null;
        }

        private void Style(MeshRenderer renderer, bool focused, bool surface, float freshness = 1f)
        {
            if (renderer == null) return;
            _props.Clear();
            if (surface)
            {
                var tint = focused ? focusEdgeColour : edgeColour;
                _props.SetColor(TintId, new Color(tint.r, tint.g, tint.b,
                    (focused ? focusSurfaceAlpha : surfaceAlpha) * freshness));
                renderer.SetPropertyBlock(_props);
                return;
            }
            var edge = focused ? focusEdgeColour : edgeColour;
            var fill = focused ? focusFillAlpha : fillAlpha;
            _props.SetColor(FillColor, new Color(edge.r, edge.g, edge.b, fill));
            _props.SetColor(EdgeColor, new Color(edge.r, edge.g, edge.b, focused ? focusEdgeAlpha : edgeAlpha));
            _props.SetFloat(EdgeWidthPxId, focused ? focusEdgeWidthPx : edgeWidthPx);
            // These box edges are diagnostics, reachable only through the explicit debug toggle.
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
            root.transform.position = o.smoothedWorldPosition;

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Highlight";
            box.transform.SetParent(root.transform, false);
            box.transform.localScale = o.smoothedWorldSize;
            // An overlay must never eat a controller ray or a physics query. Destroy() is deferred to
            // end of frame, so disable first — otherwise the collider is live for one frame.
            var collider = box.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; DestroyOwned(collider); }
            var renderer = box.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.enabled = false; // Never flash the proxy cube while waiting for Show()/depth.
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
            foreach (var pair in _visuals) if (pair.Key != null) DestroyOwned(pair.Key);
            _visuals.Clear();
            if (_material != null) DestroyOwned(_material);
            if (_surface != null) DestroyOwned(_surface);
        }

        private static void DestroyOwned(Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
