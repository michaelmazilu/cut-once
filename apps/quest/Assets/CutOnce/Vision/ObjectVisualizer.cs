using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// The scanner look: a translucent blue glow box on the object, and its name floating beside it.
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
        public Vector3 defaultSize = new Vector3(0.28f, 0.28f, 0.28f);
        public float labelHeight = 0.16f;
        public float labelSize = 0.02f;
        public bool showConfidence;

        [Tooltip("How fast visuals catch up to the tracked position, in metres/second of lerp.")]
        public float followSpeed = 8f;

        private class Cached { public Transform transform; public TextMesh text; public string lastName; }
        private readonly System.Collections.Generic.Dictionary<GameObject, Cached> _labels = new();

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
                cached = new Cached { transform = found, text = found.GetComponent<TextMesh>() };
                _labels[o.visual] = cached;
            }
            var label = cached.transform;

            if (cached.text != null && (showConfidence || cached.lastName != o.className))
            {
                cached.text.text = showConfidence
                    ? $"{o.className.ToUpperInvariant()}\n<size=10>{o.confidence * 100f:0}%</size>"
                    : o.className.ToUpperInvariant();
                cached.lastName = o.className;
            }

            // Billboard: face the headset, upright, so text is never mirrored or tilted.
            var cam = Camera.main;
            if (cam != null)
            {
                var away = label.position - cam.transform.position;
                away.y = 0f;
                if (away.sqrMagnitude > 0.0001f) label.rotation = Quaternion.LookRotation(away, Vector3.up);
            }
        }

        public void Hide(TrackedObject o)
        {
            if (o.visual == null) return;
            _labels.Remove(o.visual);
            Destroy(o.visual);
            o.visual = null;
        }

        private GameObject Build(TrackedObject o)
        {
            var root = new GameObject($"[Vision] {o.className}");
            root.transform.position = o.smoothedWorldPosition;

            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            box.name = "Highlight";
            box.transform.SetParent(root.transform, false);
            box.transform.localScale = defaultSize;
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

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(root.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, labelHeight, 0f);

            var text = labelGo.AddComponent<TextMesh>();
            text.text = o.className.ToUpperInvariant();
            text.characterSize = labelSize;
            text.fontSize = 96;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.richText = true;
            text.color = new Color(0.7f, 0.95f, 1f, 1f);

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                text.font = font;
                labelGo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }
            return root;
        }
    }
}
