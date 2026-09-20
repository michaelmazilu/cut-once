using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace CutOnce.RoomSense
{
    /// <summary>
    /// The "scan the room" effect: every surface the Quest knows about — walls, floor, ceiling,
    /// tables, couches, storage — rendered as a glowing blue overlay, revealed by a pulse that
    /// expands from the player like a sonar ping.
    ///
    /// There is deliberately NO machine learning here. Space Setup already gives us labelled,
    /// positioned geometry for the whole room through MRUK; rendering it is free, instant and
    /// deterministic, which is what a live demo wants. ML (QuestCameraKit's Sentis + YOLO sample)
    /// only adds names for loose objects, and can be layered on later without touching this.
    ///
    /// Two layers, both from the same scan:
    ///  - the GLOBAL SCENE MESH: a triangle mesh of the entire room, clutter included — the mug and
    ///    the tools ON the desk glow too, because the scan captured them as geometry;
    ///  - the labelled anchors (walls, tables, couches) as tinted shapes, so furniture pops brighter.
    ///
    /// Needs: MRUK in the scene (an [MRUK] prefab) with "Load Global Mesh" enabled in its Scene
    /// Settings, Space Setup done on the device, and com.oculus.permission.USE_SCENE in the Android
    /// manifest. In the Editor, MRUK's mock rooms stand in for a real scan.
    ///
    /// Honest limit: the scene mesh is a snapshot from scan time. Move a box after Space Setup and
    /// its glow stays where it was scanned. Rescan to refresh; live-updating glow would need the
    /// Depth API as a screen-space effect instead (heavier — ask before building it).
    /// </summary>
    public class RoomGlow : MonoBehaviour
    {
        [SerializeField, Tooltip("Draw the room-wide demo overlay. Vision always suppresses it while it owns recognition.")]
        private bool renderingEnabled = true;

        public bool RenderingEnabled
        {
            get => renderingEnabled && !RoomSenseBootstrap.DetectedObjectsOnly;
            set { renderingEnabled = value; ApplyRenderingPolicy(); }
        }

        [Tooltip("Unlit, transparent, additive-ish material. RoomGlow drives its _PulseOrigin/_PulseRadius floats.")]
        public Material glowMaterial;

        [Tooltip("Tint per anchor kind; anything unlisted gets the base colour. Walls dimmer so parts still pop.")]
        public Color baseColour = new Color(0.15f, 0.55f, 1f, 0.35f);
        public Color wallColour = new Color(0.10f, 0.35f, 0.80f, 0.18f);
        public Color tableColour = new Color(0.20f, 0.90f, 1f, 0.45f);

        [Tooltip("Glow the global scene mesh: the whole room, including loose things on surfaces.")]
        public bool glowEverything = true;
        [Tooltip("Also draw tinted shapes on labelled anchors so furniture reads brighter than walls.")]
        public bool glowLabelledShapes = true;

        [Tooltip("Metres per second the reveal pulse travels.")]
        public float pulseSpeed = 3.5f;
        [Tooltip("Seconds between automatic pulses. 0 = only pulse when Pulse() is called.")]
        public float pulseEvery = 6f;

        private readonly List<GameObject> _spawned = new();
        private readonly List<MeshRenderer> _renderers = new();
        private MaterialPropertyBlock _props;
        private float _pulseStartedAt = -999f;
        private Vector3 _pulseOrigin;
        private bool _sceneLoaded, _overlayActive;

        private static readonly int PulseOriginId = Shader.PropertyToID("_PulseOrigin");
        private static readonly int PulseRadiusId = Shader.PropertyToID("_PulseRadius");
        private static readonly int TintId = Shader.PropertyToID("_Tint");

        private void OnEnable() => ApplyRenderingPolicy();

        private void OnDisable() => SetOverlayActive(false);

        public void ApplyRenderingPolicy()
        {
            var active = isActiveAndEnabled && RenderingEnabled;
            SetOverlayActive(active);
            if (active && _sceneLoaded && _spawned.Count == 0) BuildOverlay();
        }

        private void SetOverlayActive(bool active)
        {
            _overlayActive = active;
            foreach (var go in _spawned)
                if (go != null && go.activeSelf != active) go.SetActive(active);
        }

        private void Start()
        {
            _props = new MaterialPropertyBlock();
            // MRUK loads the scene asynchronously after permissions; build the overlay when it is ready.
            MRUK.Instance.RegisterSceneLoadedCallback(BuildOverlay);
        }

        /// <summary>Fire one reveal pulse from wherever the head is now (BOTW scan ping).</summary>
        public void Pulse()
        {
            var head = Camera.main != null ? Camera.main.transform.position : transform.position;
            _pulseOrigin = head;
            _pulseStartedAt = Time.time;
        }

        private void Update()
        {
            if (_overlayActive != RenderingEnabled) ApplyRenderingPolicy();
            if (!RenderingEnabled) return;
            if (pulseEvery > 0f && Time.time - _pulseStartedAt > pulseEvery) Pulse();
            var radius = (Time.time - _pulseStartedAt) * pulseSpeed;
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_props);
                _props.SetVector(PulseOriginId, _pulseOrigin);
                _props.SetFloat(PulseRadiusId, radius);
                r.SetPropertyBlock(_props);
            }
        }

        private void BuildOverlay()
        {
            _sceneLoaded = true;
            ClearOverlay();
            if (!isActiveAndEnabled || !RenderingEnabled) return;

            var room = MRUK.Instance.GetCurrentRoom();
            if (room == null) { Debug.LogWarning("[RoomGlow] no room: run Space Setup on the headset."); return; }

            // Layer 1 — everything. The global mesh is the scan of the whole room, so anything that
            // was on the desk when Space Setup ran is in here and glows with no labelling needed.
            if (glowEverything) GlowGlobalMesh(room);

            if (!glowLabelledShapes) { Pulse(); return; }
            foreach (var anchor in room.Anchors)
            {
                // Volumes (tables, couches, storage) become glowing boxes; planes (walls, floor,
                // ceiling, doors, windows) become glowing quads, nudged off the surface to avoid z-fighting.
                if (anchor.VolumeBounds.HasValue)
                {
                    var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Prepare(box, anchor, ColourFor(anchor));
                    var b = anchor.VolumeBounds.Value;
                    box.transform.localScale = b.size;
                    box.transform.localPosition = b.center;
                }
                else if (anchor.PlaneRect.HasValue)
                {
                    var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                    Prepare(quad, anchor, ColourFor(anchor));
                    var rect = anchor.PlaneRect.Value;
                    quad.transform.localScale = new Vector3(rect.size.x, rect.size.y, 1f);
                    quad.transform.localPosition = new Vector3(rect.center.x, rect.center.y, 0.005f);
                }
            }
            Pulse();
            Debug.Log($"[RoomGlow] overlay built: {_spawned.Count} surfaces glowing.");
        }

        /// <summary>One renderer over the room's scene mesh. Shares the scanned mesh; copies nothing.</summary>
        private void GlowGlobalMesh(MRUKRoom room)
        {
            var anchor = room.GlobalMeshAnchor;
            // MRUK hands the scan back as a Mesh on the anchor itself (lazily built on first access),
            // NOT as a child MeshFilter — there is no renderer to find, so we make our own.
            var mesh = anchor != null ? anchor.GlobalMesh : null;
            if (mesh == null)
            {
                Debug.LogWarning("[RoomGlow] no global mesh. Enable 'Load Global Mesh' in the MRUK Scene Settings and rescan.");
                return;
            }
            var go = new GameObject("[RoomGlow] scene mesh");
            go.transform.SetParent(anchor.transform, false); // mesh vertices are in anchor space
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = glowMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var props = new MaterialPropertyBlock();
            props.SetColor(TintId, baseColour);
            r.SetPropertyBlock(props);
            _spawned.Add(go);
            _renderers.Add(r);
        }

        private void Prepare(GameObject go, MRUKAnchor anchor, Color tint)
        {
            go.name = $"[RoomGlow] {anchor.Label}";
            go.transform.SetParent(anchor.transform, false);
            // Only the decorative primitive's collider is removed. MRUK's geometry stays intact.
            var collider = go.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; DestroyOwned(collider); }
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = glowMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var props = new MaterialPropertyBlock();
            props.SetColor(TintId, tint);
            r.SetPropertyBlock(props);
            _spawned.Add(go);
            _renderers.Add(r);
        }

        private Color ColourFor(MRUKAnchor anchor)
        {
            var label = anchor.Label;
            if ((label & (MRUKAnchor.SceneLabels.WALL_FACE | MRUKAnchor.SceneLabels.CEILING | MRUKAnchor.SceneLabels.FLOOR)) != 0) return wallColour;
            if ((label & MRUKAnchor.SceneLabels.TABLE) != 0) return tableColour;
            return baseColour;
        }

        private void ClearOverlay()
        {
            foreach (var go in _spawned)
                if (go != null) { go.SetActive(false); DestroyOwned(go); }
            _spawned.Clear();
            _renderers.Clear();
        }

        private void OnDestroy() => ClearOverlay();

        private static void DestroyOwned(Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
