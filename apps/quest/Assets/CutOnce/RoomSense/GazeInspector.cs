using System.Collections.Generic;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace CutOnce.RoomSense
{
    /// <summary>
    /// Look at a thing, it lights up and names itself.
    ///
    /// The hard part is that the room scan is ONE mesh — there is no "bottle object" in it. So on
    /// load we split the scan into connected components (islands of welded triangles). A bottle
    /// standing on a desk is its own island, so looking at it isolates exactly that bottle.
    ///
    /// Real Quest scans are messier than synthetic ones: the mesher often fuses everything into a
    /// single island. When the island we hit is implausibly large we fall back to carving out the
    /// triangles within <see cref="fallbackRadius"/> of the gaze point, which still highlights a
    /// bottle-sized patch. Both paths are exercised by the tests.
    ///
    /// Naming is deliberately pluggable (<see cref="IObjectNamer"/>). Today it reads MRUK's own
    /// labels for furniture and falls back to a size heuristic for loose objects. Swap in a YOLO
    /// namer later via <see cref="SetNamer"/> and nothing else here changes.
    /// </summary>
    [RequireComponent(typeof(RoomGlow))]
    public class GazeInspector : MonoBehaviour
    {
        [SerializeField, Tooltip("Draw guessed gaze labels and scan highlights. Suppressed while Vision owns recognition.")]
        private bool renderingEnabled = true;

        public bool RenderingEnabled
        {
            get => renderingEnabled && !RoomSenseBootstrap.DetectedObjectsOnly;
            set { renderingEnabled = value; ApplyRenderingPolicy(); }
        }

        [Tooltip("Material for the highlighted object. Defaults to RoomGlow's glow material.")]
        public Material highlightMaterial;
        [Tooltip("Highlight tint — deliberately hotter than the ambient room glow so it reads as 'selected'.")]
        public Color highlightColour = new Color(0.6f, 1f, 1f, 0.95f);

        [Tooltip("How far you can inspect, in metres.")]
        public float maxDistance = 6f;
        [Tooltip("Times per second the gaze ray is re-cast. Cheap, but no reason to do it every frame.")]
        public float refreshHz = 15f;

        [Tooltip("An island bigger than this fraction of the whole scan is 'the room, not an object'.")]
        [Range(0.05f, 1f)] public float islandIsObjectBelow = 0.35f;
        [Tooltip("Radius carved out around the gaze point when the island is too big to be an object.")]
        public float fallbackRadius = 0.22f;

        [Tooltip("Physics layer for the gaze colliders. 2 = Ignore Raycast, so this never eats another system's ray.")]
        public int colliderLayer = 2;

        /// <summary>What the gaze is currently resting on. Null when looking at nothing.</summary>
        public GazeTarget? Current { get; private set; }

        private IObjectNamer _namer;
        private Mesh _scan;
        private Transform _scanSpace;
        private MeshCollider _collider;
        private int[] _triangles;
        private Vector3[] _vertices;
        private int[] _islandOfTriangle;     // triangle index -> island id
        private int[] _islandTriangleCount;  // island id -> triangle count
        private readonly Dictionary<int, Mesh> _islandMeshes = new();
        private readonly List<MRUKAnchor> _anchors = new();

        private GameObject _highlightGo;
        private MeshFilter _highlightFilter;
        private MeshRenderer _highlightRenderer;
        private GameObject _labelGo;
        private TextMesh _label;
        private float _nextCast;
        private int _shownIsland = -2;

        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int PulseRadiusId = Shader.PropertyToID("_PulseRadius");

        /// <summary>Replace the naming strategy — this is the seam an on-device YOLO plugs into.</summary>
        public void SetNamer(IObjectNamer namer) => _namer = namer;

        private void OnEnable() => ApplyRenderingPolicy();

        private void OnDisable() => HideVisuals();

        public void ApplyRenderingPolicy()
        {
            if (!isActiveAndEnabled || !RenderingEnabled) HideVisuals();
        }

        private void HideVisuals()
        {
            if (_highlightGo != null && _highlightGo.activeSelf) _highlightGo.SetActive(false);
            if (_labelGo != null && _labelGo.activeSelf) _labelGo.SetActive(false);
            _shownIsland = -2;
        }

        private void Awake()
        {
            _namer ??= new DefaultNamer();
            if (highlightMaterial == null)
            {
                var glow = GetComponent<RoomGlow>();
                if (glow != null) highlightMaterial = glow.glowMaterial;
            }
        }

        private void Start() => MRUK.Instance.RegisterSceneLoadedCallback(Rebuild);

        /// <summary>Index the current scan. Cheap enough to redo whenever the room changes.</summary>
        public void Rebuild()
        {
            Teardown();
            var room = MRUK.Instance.GetCurrentRoom();
            if (room == null) return;

            _anchors.Clear();
            foreach (var a in room.Anchors) if (a.VolumeBounds.HasValue || a.PlaneRect.HasValue) _anchors.Add(a);

            var anchor = room.GlobalMeshAnchor;
            _scan = anchor != null ? anchor.GlobalMesh : null;
            if (_scan == null)
            {
                Debug.LogWarning("[GazeInspector] no global mesh; only labelled anchors can be inspected.");
                return;
            }

            _scanSpace = anchor.transform;
            _vertices = _scan.vertices;
            _triangles = _scan.triangles;
            BuildIslands();

            var go = new GameObject("[Gaze] scan collider") { layer = colliderLayer };
            go.transform.SetParent(_scanSpace, false);
            _collider = go.AddComponent<MeshCollider>();
            _collider.sharedMesh = _scan;

            Debug.Log($"[GazeInspector] indexed {_triangles.Length / 3} triangles into {_islandTriangleCount.Length} islands.");
        }

        /// <summary>Union-find over triangles that share a welded vertex position.</summary>
        private void BuildIslands()
        {
            var triCount = _triangles.Length / 3;
            var weld = new Dictionary<Vector3Int, int>(_vertices.Length);
            var vertexKey = new int[_vertices.Length];
            for (var v = 0; v < _vertices.Length; v++)
            {
                // 1mm welding grid: distinct objects never share a vertex, split surfaces still merge.
                var p = _vertices[v];
                var key = new Vector3Int(Mathf.RoundToInt(p.x * 1000f), Mathf.RoundToInt(p.y * 1000f), Mathf.RoundToInt(p.z * 1000f));
                if (!weld.TryGetValue(key, out var id)) { id = weld.Count; weld[key] = id; }
                vertexKey[v] = id;
            }

            var parent = new int[triCount];
            for (var i = 0; i < triCount; i++) parent[i] = i;

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }

            var firstTriangleAtVertex = new Dictionary<int, int>(weld.Count);
            for (var t = 0; t < triCount; t++)
            {
                for (var k = 0; k < 3; k++)
                {
                    var vk = vertexKey[_triangles[t * 3 + k]];
                    if (firstTriangleAtVertex.TryGetValue(vk, out var other)) Union(other, t);
                    else firstTriangleAtVertex[vk] = t;
                }
            }

            _islandOfTriangle = new int[triCount];
            var ids = new Dictionary<int, int>();
            var counts = new List<int>();
            for (var t = 0; t < triCount; t++)
            {
                var root = Find(t);
                if (!ids.TryGetValue(root, out var id)) { id = counts.Count; ids[root] = id; counts.Add(0); }
                _islandOfTriangle[t] = id;
                counts[id]++;
            }
            _islandTriangleCount = counts.ToArray();
        }

        private void Update()
        {
            if (!RenderingEnabled) { HideVisuals(); return; }
            var cam = Camera.main;
            if (cam == null || _collider == null) return;
            if (Time.time < _nextCast) { FaceCamera(cam); return; }
            _nextCast = Time.time + 1f / Mathf.Max(1f, refreshHz);

            Current = Inspect(new Ray(cam.transform.position, cam.transform.forward));
            Show(Current);
            FaceCamera(cam);
        }

        /// <summary>Cast a ray into the scan and work out what is being looked at. Pure enough to test.</summary>
        public GazeTarget? Inspect(Ray ray)
        {
            if (_collider == null) return null;
            if (!Physics.Raycast(ray, out var hit, maxDistance, 1 << colliderLayer)) return null;
            if (hit.collider != _collider || hit.triangleIndex < 0) return null;

            var island = _islandOfTriangle[hit.triangleIndex];
            var fraction = _islandTriangleCount[island] / (float)(_triangles.Length / 3);
            var isolated = fraction <= islandIsObjectBelow;

            var mesh = isolated ? IslandMesh(island) : PatchMesh(hit.point);
            if (mesh == null || mesh.vertexCount == 0) return null;

            var worldCentre = _scanSpace.TransformPoint(mesh.bounds.center);
            var size = Vector3.Scale(mesh.bounds.size, _scanSpace.lossyScale);
            var anchor = AnchorContaining(worldCentre);

            var target = new GazeTarget
            {
                mesh = mesh,
                islandId = isolated ? island : -1,
                worldCentre = worldCentre,
                worldSize = size,
                triangleCount = mesh.triangles.Length / 3,
                anchor = anchor,
                hitPoint = hit.point,
            };
            target.name = _namer.Name(target);
            return target;
        }

        private Mesh IslandMesh(int island)
        {
            if (_islandMeshes.TryGetValue(island, out var cached)) return cached;
            var keep = new List<int>();
            for (var t = 0; t < _islandOfTriangle.Length; t++)
                if (_islandOfTriangle[t] == island) { keep.Add(_triangles[t * 3]); keep.Add(_triangles[t * 3 + 1]); keep.Add(_triangles[t * 3 + 2]); }
            var mesh = Compact(keep);
            _islandMeshes[island] = mesh;
            return mesh;
        }

        /// <summary>Fallback for fused scans: everything within a bottle's reach of the gaze point.</summary>
        private Mesh PatchMesh(Vector3 localHit)
        {
            var r2 = fallbackRadius * fallbackRadius;
            var keep = new List<int>();
            for (var t = 0; t < _triangles.Length / 3; t++)
            {
                // Every vertex must be inside the radius, not just the centroid: a single big
                // triangle (a whole desktop, a whole wall) has its centre near everything on it,
                // and letting one in drags metres of geometry into a bottle-sized selection.
                var a = _vertices[_triangles[t * 3]];
                if ((a - localHit).sqrMagnitude > r2) continue;
                var b = _vertices[_triangles[t * 3 + 1]];
                if ((b - localHit).sqrMagnitude > r2) continue;
                var c = _vertices[_triangles[t * 3 + 2]];
                if ((c - localHit).sqrMagnitude > r2) continue;
                keep.Add(_triangles[t * 3]); keep.Add(_triangles[t * 3 + 1]); keep.Add(_triangles[t * 3 + 2]);
            }
            return Compact(keep);
        }

        private Mesh Compact(List<int> indices)
        {
            var remap = new Dictionary<int, int>();
            var verts = new List<Vector3>();
            var tris = new List<int>(indices.Count);
            foreach (var i in indices)
            {
                if (!remap.TryGetValue(i, out var n)) { n = verts.Count; remap[i] = n; verts.Add(_vertices[i]); }
                tris.Add(n);
            }
            var mesh = new Mesh { name = "[Gaze] selection" };
            if (verts.Count > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private MRUKAnchor AnchorContaining(Vector3 worldPoint)
        {
            MRUKAnchor best = null;
            var bestVolume = float.MaxValue;
            foreach (var a in _anchors)
            {
                if (!a.VolumeBounds.HasValue) continue;
                var b = a.VolumeBounds.Value;
                var local = a.transform.InverseTransformPoint(worldPoint);
                if (!b.Contains(local)) continue;
                var volume = b.size.x * b.size.y * b.size.z;   // smallest containing volume wins
                if (volume < bestVolume) { bestVolume = volume; best = a; }
            }
            return best;
        }

        private void Show(GazeTarget? target)
        {
            if (!RenderingEnabled || target == null)
            {
                HideVisuals();
                return;
            }

            var t = target.Value;
            EnsureVisuals();
            _highlightGo.SetActive(true);
            _labelGo.SetActive(true);

            if (t.islandId != _shownIsland || t.islandId == -1)
            {
                _highlightFilter.sharedMesh = t.mesh;
                _shownIsland = t.islandId;
            }
            _highlightGo.transform.SetParent(_scanSpace, false);
            _highlightGo.transform.localPosition = Vector3.zero;
            _highlightGo.transform.localRotation = Quaternion.identity;

            // Park the label just off the object's shoulder so it never covers what you are looking at.
            var offset = Mathf.Max(t.worldSize.x, t.worldSize.z) * 0.5f + 0.12f;
            _labelGo.transform.position = t.worldCentre + Vector3.right * offset + Vector3.up * (t.worldSize.y * 0.5f + 0.06f);
            _label.text = $"{t.name}\n<size=8>{Mathf.RoundToInt(t.worldSize.x * 100)} × {Mathf.RoundToInt(t.worldSize.y * 100)} × {Mathf.RoundToInt(t.worldSize.z * 100)} cm</size>";
        }

        private void EnsureVisuals()
        {
            if (_highlightGo == null)
            {
                _highlightGo = new GameObject("[Gaze] highlight") { layer = colliderLayer };
                _highlightFilter = _highlightGo.AddComponent<MeshFilter>();
                _highlightRenderer = _highlightGo.AddComponent<MeshRenderer>();
                _highlightRenderer.sharedMaterial = highlightMaterial;
                _highlightRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var props = new MaterialPropertyBlock();
                props.SetColor(TintId, highlightColour);
                props.SetFloat(PulseRadiusId, 9999f);   // always visible, never waits for the pulse
                _highlightRenderer.SetPropertyBlock(props);
            }
            if (_labelGo != null) return;

            _labelGo = new GameObject("[Gaze] label") { layer = colliderLayer };
            _label = _labelGo.AddComponent<TextMesh>();
            _label.characterSize = 0.035f;
            _label.fontSize = 64;
            _label.anchor = TextAnchor.MiddleLeft;
            _label.alignment = TextAlignment.Left;
            _label.richText = true;
            _label.color = highlightColour;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null)
            {
                _label.font = font;
                _labelGo.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            }
        }

        private void FaceCamera(Camera cam)
        {
            if (_labelGo == null || !_labelGo.activeSelf) return;
            _labelGo.transform.rotation = Quaternion.LookRotation(_labelGo.transform.position - cam.transform.position);
        }

        private void Teardown()
        {
            foreach (var m in _islandMeshes.Values) if (m != null) DestroyOwned(m);
            _islandMeshes.Clear();
            if (_collider != null) DestroyOwned(_collider.gameObject);
            _collider = null;
            _shownIsland = -2;
        }

        private void OnDestroy()
        {
            HideVisuals();
            Teardown();
            if (_highlightGo != null) DestroyOwned(_highlightGo);
            if (_labelGo != null) DestroyOwned(_labelGo);
        }

        private static void DestroyOwned(Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }

    /// <summary>What the gaze landed on, with everything a namer needs to identify it.</summary>
    public struct GazeTarget
    {
        public Mesh mesh;
        public int islandId;          // -1 when carved out of a fused scan rather than a real island
        public Vector3 worldCentre;
        public Vector3 worldSize;
        public int triangleCount;
        public MRUKAnchor anchor;     // the labelled anchor containing it, if any
        public Vector3 hitPoint;
        public string name;
    }

    /// <summary>The seam an on-device model plugs into. Implement this and call SetNamer.</summary>
    public interface IObjectNamer
    {
        string Name(GazeTarget target);
    }

    /// <summary>
    /// Names things without any ML: MRUK's own labels where they exist, otherwise a size heuristic.
    ///
    /// Be honest about what this is — the heuristic is measuring a bounding box, not recognising an
    /// object. It calls a 7cm × 25cm upright thing a bottle because that is what such a thing
    /// usually is, and it says "Object" when it has no idea rather than inventing a name.
    /// </summary>
    public class DefaultNamer : IObjectNamer
    {
        private struct Shape
        {
            public string name;
            public float minH, maxH, maxFootprint, minFootprint;
        }

        private static readonly Shape[] Shapes =
        {
            new() { name = "Bottle",   minH = 0.14f, maxH = 0.34f, minFootprint = 0.03f, maxFootprint = 0.12f },
            new() { name = "Cup",      minH = 0.06f, maxH = 0.14f, minFootprint = 0.05f, maxFootprint = 0.13f },
            new() { name = "Laptop",   minH = 0.15f, maxH = 0.30f, minFootprint = 0.22f, maxFootprint = 0.42f },
            new() { name = "Box",      minH = 0.25f, maxH = 0.60f, minFootprint = 0.25f, maxFootprint = 0.70f },
            new() { name = "Chair",    minH = 0.60f, maxH = 1.10f, minFootprint = 0.35f, maxFootprint = 0.80f },
        };

        public string Name(GazeTarget t)
        {
            if (t.anchor != null)
            {
                var friendly = Friendly(t.anchor.Label);
                if (friendly != null) return friendly;
            }

            var h = t.worldSize.y;
            var footprint = Mathf.Max(t.worldSize.x, t.worldSize.z);
            foreach (var s in Shapes)
                if (h >= s.minH && h <= s.maxH && footprint >= s.minFootprint && footprint <= s.maxFootprint)
                    return s.name;

            if (t.worldSize.magnitude < 0.12f) return "Small object";
            return "Object";
        }

        private static string Friendly(MRUKAnchor.SceneLabels label)
        {
            if ((label & MRUKAnchor.SceneLabels.TABLE) != 0) return "Desk";
            if ((label & MRUKAnchor.SceneLabels.SCREEN) != 0) return "Monitor";
            if ((label & MRUKAnchor.SceneLabels.STORAGE) != 0) return "Shelf";
            if ((label & MRUKAnchor.SceneLabels.COUCH) != 0) return "Couch";
            if ((label & MRUKAnchor.SceneLabels.BED) != 0) return "Bed";
            if ((label & MRUKAnchor.SceneLabels.LAMP) != 0) return "Lamp";
            if ((label & MRUKAnchor.SceneLabels.PLANT) != 0) return "Plant";
            if ((label & MRUKAnchor.SceneLabels.DOOR_FRAME) != 0) return "Door";
            if ((label & MRUKAnchor.SceneLabels.WINDOW_FRAME) != 0) return "Window";
            if ((label & MRUKAnchor.SceneLabels.WALL_ART) != 0) return "Wall art";
            return null;   // OTHER and friends carry no useful name; let the heuristic try
        }
    }
}
