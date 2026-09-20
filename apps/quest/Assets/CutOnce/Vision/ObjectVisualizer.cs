using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// Compact, sentence-case labels. Approximate detection boxes are an opt-in diagnostic.
    ///
    /// Reuses RoomSense's SheikahGlow material (loaded from Resources so the shader survives player
    /// build stripping — a shader only reached via Shader.Find can be dropped, giving a pink object
    /// on device that looked perfect in the Editor). The NAME never comes from RoomSense: it is
    /// whatever the vision model called the thing.
    ///
    /// Visuals follow the tracked object's SMOOTHED position, never the raw per-frame measurement,
    /// which is the difference between a label that sits on the bottle and one that vibrates.
    /// </summary>
    public class ObjectVisualizer : MonoBehaviour
    {
        public Material glowMaterial;
        public Color highlightColour = new Color(0.25f, 0.75f, 1f, 0.55f);
        [Tooltip("Only for an object whose size could not be measured: everything else is drawn at the size its detection box works out to.")]
        public Vector3 defaultSize = new Vector3(0.28f, 0.28f, 0.28f);

        /// <summary>The one being looked at, drawn a little stronger so a roomful of glows still has a subject.</summary>
        public TrackedObject Focused { get; set; }
        public float labelHeight = 0.08f;
        public float labelSize = 0.0035f;
        public bool showConfidence;
        public bool showBounds;

        [Tooltip("How fast visuals catch up to the tracked position, in metres/second of lerp.")]
        public float followSpeed = 8f;

        private class Cached { public Transform transform, box; public MeshRenderer glow; public TextMesh text, shadow; public string lastName; public bool wasFocused = true; }
        private readonly System.Collections.Generic.Dictionary<GameObject, Cached> _labels = new();
        private Transform _camera;
        private MaterialPropertyBlock _props;

        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int PulseRadiusId = Shader.PropertyToID("_PulseRadius");

        private void Awake()
        {
            if (glowMaterial == null) glowMaterial = Resources.Load<Material>("SheikahGlow");
            if (glowMaterial == null)
                Debug.LogWarning("[Vision] no glow material; detections will have labels but no highlight.");
        }

        /// <summary>Create or update the visual for a tracked object.</summary>
        public void Show(TrackedObject o)
        {
            if (o.visual == null) o.visual = Build(o);
            if (!o.visual.activeSelf) o.visual.SetActive(true);

            var target = o.smoothedWorldPosition;
            var t = o.visual.transform;
            // Lerp on top of the EMA: the EMA settles the measurement, this settles the rendering.
            t.position = Vector3.Lerp(t.position, target, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));

            // Cached on the visual: transform.Find by string plus a fresh interpolated string every
            // frame, per tracked object, is a real per-frame cost on an XR2.
            if (!_labels.TryGetValue(o.visual, out var cached))
            {
                var found = t.Find("Label");
                if (found == null) return;
                var box = t.Find("Highlight");
                cached = new Cached { transform = found, text = found.GetComponent<TextMesh>(), box = box, glow = box != null ? box.GetComponent<MeshRenderer>() : null };
                _labels[o.visual] = cached;
            }
            Fit(o, cached);
            var label = cached.transform;

            if (cached.text != null && (showConfidence || cached.lastName != o.className))
            {
                cached.text.text = showConfidence
                    ? $"{o.className} · {o.confidence * 100f:0}%"
                    : o.className;
                cached.lastName = o.className;
                if (cached.shadow != null) cached.shadow.text = cached.text.text;
            }

            // Billboard: face the headset, upright, so text is never mirrored or tilted.
            if (_camera == null && Camera.main != null) _camera = Camera.main.transform;
            if (_camera != null)
            {
                var away = label.position - _camera.position;
                away.y = 0f;
                if (away.sqrMagnitude > 0.0001f) label.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }

        public void Hide(TrackedObject o)
        {
            if (o.visual != null && o.visual.activeSelf) o.visual.SetActive(false);
        }

        public void Release(TrackedObject o)
        {
            if (o.visual == null) return;
            _labels.Remove(o.visual);
            Destroy(o.visual);
            o.visual = null;
        }

        /// <summary>
        /// The highlight is the size the thing measured, not a cube. Eased like the position, so a box that grows by
        /// a centimetre between frames does not flicker, and the one being looked at is drawn a little stronger.
        /// Everything here is cached: this runs per object per frame, where a string Find or a fresh property block
        /// is exactly the per-frame cost AGENTS rule 10 is about.
        /// </summary>
        private void Fit(TrackedObject o, Cached cached)
        {
            if (cached.box == null) return;
            var want = o.smoothedWorldSize == Vector3.zero ? defaultSize : o.smoothedWorldSize;
            cached.box.localScale = Vector3.Lerp(cached.box.localScale, want, 1f - Mathf.Exp(-followSpeed * Time.deltaTime));
            cached.transform.localPosition = new Vector3(0f, cached.box.localScale.y * 0.5f + labelHeight, 0f);
            var focused = o == Focused;
            if (cached.glow == null || focused == cached.wasFocused) return;   // the colour only changes when what you look at does
            cached.wasFocused = focused;
            _props ??= new MaterialPropertyBlock();
            var colour = highlightColour;
            colour.a *= focused ? 1f : 0.55f;
            _props.SetColor(TintId, colour);
            _props.SetFloat(PulseRadiusId, 9999f);
            cached.glow.SetPropertyBlock(_props);
        }

        private GameObject Build(TrackedObject o)
        {
            var root = new GameObject($"[Vision] {o.className}");
            root.transform.position = o.smoothedWorldPosition;

            if (showBounds && glowMaterial != null)
            {
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "Highlight";
                box.transform.SetParent(root.transform, false);
                box.transform.localScale = o.smoothedWorldSize == Vector3.zero ? defaultSize : o.smoothedWorldSize;
                // An overlay must never eat a controller ray or a physics query. Destroy() is deferred to
                // end of frame, so disable first — otherwise the collider is live for one frame.
                var collider = box.GetComponent<Collider>();
                if (collider != null) { collider.enabled = false; Destroy(collider); }

                var renderer = box.GetComponent<MeshRenderer>();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                if (glowMaterial != null)
                {
                    renderer.sharedMaterial = glowMaterial;
                    var props = new MaterialPropertyBlock();
                    props.SetColor(TintId, highlightColour);
                    props.SetFloat(PulseRadiusId, 9999f);   // always visible; don't wait on RoomSense's pulse
                    renderer.SetPropertyBlock(props);
                }
            }

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(root.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, (o.smoothedWorldSize == Vector3.zero ? defaultSize : o.smoothedWorldSize).y * 0.5f + labelHeight, 0f);

            var text = labelGo.AddComponent<TextMesh>();
            text.text = o.className;
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
            shadow.text = text.text; shadow.font = text.font; shadow.fontSize = text.fontSize;
            shadow.characterSize = text.characterSize; shadow.anchor = text.anchor;
            shadow.alignment = text.alignment; shadow.richText = false;
            shadow.color = new Color(0.02f, 0.03f, 0.03f, 1f);
            shadowGo.GetComponent<MeshRenderer>().sharedMaterial = text.GetComponent<MeshRenderer>().sharedMaterial;
            // The highlight goes in the cache with the label: Fit() resizes and dims it every frame and must not go
            // looking for it by name.
            var built = root.transform.Find("Highlight");
            _labels[root] = new Cached { transform = labelGo.transform, text = text, shadow = shadow, box = built, glow = built != null ? built.GetComponent<MeshRenderer>() : null };
            return root;
        }

        private void OnDestroy()
        {
            foreach (var pair in _labels) if (pair.Key != null) Destroy(pair.Key);
            _labels.Clear();
        }
    }
}
