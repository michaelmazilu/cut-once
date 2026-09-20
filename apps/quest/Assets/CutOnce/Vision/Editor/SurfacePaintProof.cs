using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CutOnce.Vision.Editor
{
    /// <summary>
    /// GPU proof of the production SurfaceGlow shader, with known synthetic environment depth.
    /// Run with graphics enabled: -batchmode -executeMethod CutOnce.Vision.Editor.SurfacePaintProof.Run.
    /// These captures validate rendering/reprojection, not object detection or Quest passthrough.
    /// </summary>
    public static class SurfacePaintProof
    {
        private const int Width = 960, Height = 640;
        private const int DepthWidth = 480, DepthHeight = 320;
        private const int FixtureLayer = 30;
        private const int Margin = 4;
        private const float PixelTolerance = 3f / 255f;
        private const float BlueThreshold = 12f / 255f;
        private enum Surface { Background, Table, Bottle, Occluder, Wall, Floor }

        [Serializable]
        private sealed class ProofReport
        {
            public string scope = "Synthetic fixture rendered by the production CutOnce/SurfaceGlow shader. " +
                "Not a headset capture; does not validate live detection, depth noise, passthrough, stereo or frame time.";
            public string unity;
            public string graphics;
            public string shader = "CutOnce/SurfaceGlow";
            public int width = Width, height = Height;
            public int depthWidth = DepthWidth, depthHeight = DepthHeight;
            public int edgeExclusionPixels = Margin;
            public float minimumBlueIncrease = BlueThreshold;
            public float unchangedPixelTolerance = PixelTolerance;
            public bool passed;
            public string error;
            public List<ViewResult> views = new List<ViewResult>();
        }

        [Serializable]
        private sealed class ViewResult
        {
            public string name;
            public string baseline, highlighted, noDepth, invalidDepth;
            public float depthCameraOffsetMetres;
            public bool passed;
            public List<Check> checks = new List<Check>();
        }

        [Serializable]
        private sealed class Check
        {
            public string name;
            public int sampledPixels, matchingPixels;
            public float matchingFraction;
            public float requiredFraction;
            public bool passed;
        }

        private sealed class Fixture : IDisposable
        {
            public GameObject root;
            public Camera camera;
            public Material paint;
            public readonly List<Renderer> proxies = new List<Renderer>();
            public readonly Dictionary<int, Surface> surfaces = new Dictionary<int, Surface>();
            public readonly List<Object> resources = new List<Object>();

            public void Dispose()
            {
                if (root != null) Object.DestroyImmediate(root);
                foreach (var resource in resources)
                    if (resource != null) Object.DestroyImmediate(resource);
            }
        }

        public static void Run()
        {
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/cli/surface-proof"));
            Directory.CreateDirectory(directory);
            var report = new ProofReport { unity = Application.unityVersion, graphics = SystemInfo.graphicsDeviceName };
            var oldTexture = Shader.GetGlobalTexture("_EnvironmentDepthTexture");
            var oldTexelSize = Shader.GetGlobalVector("_EnvironmentDepthTexture_TexelSize");
            var oldDepthParameters = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");
            var oldMatrices = Shader.GetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices");
            var oldHard = Shader.IsKeywordEnabled("HARD_OCCLUSION");
            var oldSoft = Shader.IsKeywordEnabled("SOFT_OCCLUSION");

            try
            {
                if (!Application.isBatchMode)
                    throw new InvalidOperationException("SurfacePaintProof.Run requires a separate batchmode Editor; it creates a temporary scene.");
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                    throw new InvalidOperationException("Surface proof requires GPU rendering. Remove -nographics from the Unity command.");

                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                using (var fixture = CreateFixture())
                {
                    report.views.Add(RenderView(fixture, directory, "front",
                        new Vector3(2.35f, 1.85f, -3.5f), new Vector3(0f, .68f, 0f), 0f));
                    report.views.Add(RenderView(fixture, directory, "side",
                        new Vector3(-2.2f, 1.5f, -3.1f), new Vector3(0f, .68f, 0f), .03f));
                }
                report.passed = report.views.TrueForAll(view => view.passed);
                if (!report.passed) report.error = "One or more shader pixel assertions failed; inspect checks and PNG captures.";
            }
            catch (Exception error)
            {
                report.error = error.ToString();
                report.passed = false;
                Debug.LogException(error);
            }
            finally
            {
                Shader.SetGlobalTexture("_EnvironmentDepthTexture", oldTexture);
                Shader.SetGlobalVector("_EnvironmentDepthTexture_TexelSize", oldTexelSize);
                Shader.SetGlobalVector("_EnvironmentDepthZBufferParams", oldDepthParameters);
                if (oldMatrices != null && oldMatrices.Length > 0)
                    Shader.SetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices", oldMatrices);
                SetGlobalKeyword("HARD_OCCLUSION", oldHard);
                SetGlobalKeyword("SOFT_OCCLUSION", oldSoft);
                File.WriteAllText(Path.Combine(directory, "report.json"), JsonUtility.ToJson(report, true));
            }

            Debug.Log($"Surface paint GPU proof: {(report.passed ? "PASS" : "FAIL")}. Synthetic captures: {directory}");
            if (Application.isBatchMode) EditorApplication.Exit(report.passed ? 0 : 1);
        }

        private static Fixture CreateFixture()
        {
            var fixture = new Fixture { root = new GameObject("Synthetic surface proof fixture") };
            var shader = Resources.Load<Shader>("SurfaceGlow");
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("Production SurfaceGlow shader is missing or unsupported on this GPU.");
            fixture.paint = new Material(shader);
            CheckShader(shader);
            fixture.resources.Add(fixture.paint);
            fixture.paint.SetColor("_Tint", new Color(.05f, .65f, 1f, .25f));
            fixture.paint.SetFloat("_GridSpacing", .06f);
            fixture.paint.SetFloat("_GridStrength", .2f);
            fixture.paint.SetFloat("_EdgeStrength", .75f);

            // The visible objects are an ordinary 3D fixture. Only the invisible clipping bounds use
            // SurfaceGlow. Raycast geometry supplies known depth independently of the shader.
            Add(fixture, PrimitiveType.Cube, Surface.Table, "Table top", new Vector3(0, 1.05f, 0),
                new Vector3(1.8f, .10f, .8f), new Color(.33f, .35f, .38f));
            foreach (var x in new[] { -.67f, .67f })
            {
                Add(fixture, PrimitiveType.Cube, Surface.Table, "Table upright", new Vector3(x, .55f, 0),
                    new Vector3(.11f, .9f, .14f), new Color(.23f, .25f, .28f));
                Add(fixture, PrimitiveType.Cube, Surface.Table, "Table foot", new Vector3(x, .065f, 0),
                    new Vector3(.15f, .09f, .94f), new Color(.28f, .30f, .33f));
            }
            Add(fixture, PrimitiveType.Cylinder, Surface.Bottle, "Bottle body", new Vector3(-.32f, 1.33f, -.08f),
                new Vector3(.17f, .23f, .17f), new Color(.26f, .29f, .31f));
            Add(fixture, PrimitiveType.Cylinder, Surface.Bottle, "Bottle neck", new Vector3(-.32f, 1.585f, -.08f),
                new Vector3(.095f, .035f, .095f), new Color(.20f, .22f, .24f));
            Add(fixture, PrimitiveType.Cube, Surface.Occluder, "Foreground occluder", new Vector3(.48f, .68f, -.87f),
                new Vector3(.2f, 1.25f, .12f), new Color(.43f, .32f, .23f));
            Add(fixture, PrimitiveType.Cube, Surface.Wall, "Wall behind table", new Vector3(0, 1.5f, 1.7f),
                new Vector3(8f, 3f, .08f), new Color(.17f, .19f, .22f));
            Add(fixture, PrimitiveType.Cube, Surface.Floor, "Floor below bounds", new Vector3(0, -.07f, 0),
                new Vector3(8f, .06f, 8f), new Color(.12f, .14f, .17f));
            AddProxy(fixture, "Table clipping bounds", new Vector3(0, .5725f, 0), new Vector3(1.95f, 1.135f, 1.04f));
            AddProxy(fixture, "Bottle clipping bounds", new Vector3(-.32f, 1.37f, -.08f), new Vector3(.22f, .56f, .22f));

            var cameraObject = new GameObject("Synthetic capture camera");
            cameraObject.transform.SetParent(fixture.root.transform);
            fixture.camera = cameraObject.AddComponent<Camera>();
            fixture.camera.enabled = false;
            fixture.camera.cullingMask = 1 << FixtureLayer;
            fixture.camera.clearFlags = CameraClearFlags.SolidColor;
            fixture.camera.backgroundColor = new Color(.075f, .09f, .12f, 0f);
            fixture.camera.nearClipPlane = .05f;
            fixture.camera.farClipPlane = 12f;
            fixture.camera.fieldOfView = 41f;
            fixture.camera.aspect = (float)Width / Height;
            fixture.camera.allowHDR = false;
            fixture.camera.allowMSAA = false;
            fixture.camera.stereoTargetEye = StereoTargetEyeMask.None;
            Physics.SyncTransforms();
            return fixture;
        }

        private static void Add(Fixture fixture, PrimitiveType shape, Surface surface, string name,
            Vector3 position, Vector3 scale, Color color)
        {
            var item = GameObject.CreatePrimitive(shape);
            item.name = name;
            item.layer = FixtureLayer;
            item.transform.SetParent(fixture.root.transform);
            item.transform.position = position;
            item.transform.localScale = scale;
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            material.SetColor("_BaseColor", color);
            fixture.resources.Add(material);
            item.GetComponent<Renderer>().sharedMaterial = material;
            if (shape == PrimitiveType.Cylinder)
            {
                // Unity's primitive cylinder has a rounded capsule collider; use the actual mesh
                // so our independent depth ground truth matches its flat caps and narrow neck.
                Object.DestroyImmediate(item.GetComponent<Collider>());
                item.AddComponent<MeshCollider>().sharedMesh = item.GetComponent<MeshFilter>().sharedMesh;
            }
            fixture.surfaces.Add(item.GetComponent<Collider>().GetInstanceID(), surface);
        }

        private static void AddProxy(Fixture fixture, string name, Vector3 position, Vector3 size)
        {
            var item = GameObject.CreatePrimitive(PrimitiveType.Cube);
            item.name = name;
            item.layer = FixtureLayer;
            item.transform.SetParent(fixture.root.transform);
            item.transform.position = position;
            item.transform.localScale = size;
            Object.DestroyImmediate(item.GetComponent<Collider>());
            var renderer = item.GetComponent<Renderer>();
            renderer.sharedMaterial = fixture.paint;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            fixture.proxies.Add(renderer);
        }

        private static ViewResult RenderView(Fixture fixture, string directory, string name, Vector3 eye, Vector3 target,
            float depthCameraOffset)
        {
            var camera = fixture.camera;
            camera.transform.position = eye;
            camera.transform.LookAt(target);
            var projection = Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, camera.nearClipPlane, camera.farClipPlane);
            camera.projectionMatrix = projection;
            // Meta depth always uses symmetric OpenGL NDC, including on Metal/Vulkan. Do not use
            // GL.GetGPUProjectionMatrix here: the SDK texture does not use the GPU's reversed Z.
            var depthOffset = camera.transform.right * depthCameraOffset;
            var reprojection = projection * camera.worldToCameraMatrix * Matrix4x4.Translate(-depthOffset);
            var depth = MakeDepth(fixture, reprojection);
            Shader.SetGlobalTexture("_EnvironmentDepthTexture", depth);
            Shader.SetGlobalVector("_EnvironmentDepthTexture_TexelSize", new Vector4(1f / DepthWidth, 1f / DepthHeight, DepthWidth, DepthHeight));
            Shader.SetGlobalMatrixArray("_EnvironmentDepthReprojectionMatrices", new[] { reprojection, reprojection });
            var near = camera.nearClipPlane;
            var far = camera.farClipPlane;
            Shader.SetGlobalVector("_EnvironmentDepthZBufferParams", new Vector4(-2f * far * near / (far - near), -(far + near) / (far - near), 0, 0));

            var result = new ViewResult
            {
                name = name,
                baseline = $"synthetic-{name}-01-baseline.png",
                highlighted = $"synthetic-{name}-02-surface-highlight.png",
                noDepth = $"synthetic-{name}-03-depth-unavailable.png",
                invalidDepth = $"synthetic-{name}-04-invalid-depth.png",
                depthCameraOffsetMetres = depthCameraOffset
            };
            Texture2D baseline = null, painted = null, noDepth = null, invalidDepth = null;
            try
            {
                foreach (var proxy in fixture.proxies) proxy.enabled = false;
                baseline = Capture(camera);
                Save(baseline, directory, result.baseline);
                foreach (var proxy in fixture.proxies) proxy.enabled = true;
                SetDepthKeywords(fixture.paint, true);
                painted = Capture(camera);
                Save(painted, directory, result.highlighted);
                SetDepthKeywords(fixture.paint, false);
                noDepth = Capture(camera);
                Save(noDepth, directory, result.noDepth);
                var invalid = new float[DepthWidth * DepthHeight];
                for (var index = 0; index < invalid.Length; index++) invalid[index] = 1f;
                depth.SetPixelData(invalid, 0);
                depth.Apply(false, false);
                SetDepthKeywords(fixture.paint, true);
                invalidDepth = Capture(camera);
                Save(invalidDepth, directory, result.invalidDepth);
                CheckShader(fixture.paint.shader);
                Compare(fixture, baseline.GetPixels32(), painted.GetPixels32(), noDepth.GetPixels32(), invalidDepth.GetPixels32(), result);
                SaveComparison(baseline, painted, directory, $"synthetic-{name}-comparison.png");
                result.passed = result.checks.TrueForAll(check => check.passed);
                foreach (var check in result.checks)
                    Debug.Log($"Surface proof {name}/{check.name}: {check.matchingPixels}/{check.sampledPixels}, {(check.passed ? "PASS" : "FAIL")}");
            }
            finally
            {
                Object.DestroyImmediate(depth);
                if (baseline != null) Object.DestroyImmediate(baseline);
                if (painted != null) Object.DestroyImmediate(painted);
                if (noDepth != null) Object.DestroyImmediate(noDepth);
                if (invalidDepth != null) Object.DestroyImmediate(invalidDepth);
            }
            return result;
        }

        private static Texture2D MakeDepth(Fixture fixture, Matrix4x4 reprojection)
        {
            var pixels = new float[DepthWidth * DepthHeight];
            var inverse = reprojection.inverse;
            for (var y = 0; y < DepthHeight; y++)
            for (var x = 0; x < DepthWidth; x++)
            {
                var nx = (x + .5f) / DepthWidth * 2f - 1f;
                var ny = (y + .5f) / DepthHeight * 2f - 1f;
                var start = inverse.MultiplyPoint(new Vector3(nx, ny, -1f));
                var end = inverse.MultiplyPoint(new Vector3(nx, ny, 1f));
                var ray = new Ray(start, (end - start).normalized);
                var raw = 1f;
                if (Physics.Raycast(ray, out var hit, fixture.camera.farClipPlane, 1 << FixtureLayer, QueryTriggerInteraction.Ignore))
                {
                    var projected = reprojection * new Vector4(hit.point.x, hit.point.y, hit.point.z, 1);
                    raw = (projected.z / projected.w + 1f) * .5f;
                }
                pixels[y * DepthWidth + x] = raw;
            }
            var texture = new Texture2D(DepthWidth, DepthHeight, TextureFormat.RFloat, false, true)
            { name = "Synthetic known environment depth", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D Capture(Camera camera)
        {
            var target = RenderTexture.GetTemporary(Width, Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                var request = new RenderPipeline.StandardRequest { destination = target };
                if (!RenderPipeline.SupportsRenderRequest(camera, request))
                    throw new InvalidOperationException("The active render pipeline does not support the surface-proof camera request.");
                RenderPipeline.SubmitRenderRequest(camera, request);
                RenderTexture.active = target;
                var image = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                image.Apply(false, false);
                return image;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }

        private static void Compare(Fixture fixture, Color32[] baseline, Color32[] painted, Color32[] noDepth,
            Color32[] invalidDepth, ViewResult result)
        {
            var labels = new Surface[Width * Height];
            var insideBounds = new bool[labels.Length];
            for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var index = y * Width + x;
                var ray = fixture.camera.ViewportPointToRay(new Vector3((x + .5f) / Width, (y + .5f) / Height, 0));
                if (Physics.Raycast(ray, out var hit, fixture.camera.farClipPlane, 1 << FixtureLayer, QueryTriggerInteraction.Ignore))
                    labels[index] = fixture.surfaces[hit.collider.GetInstanceID()];
                // Assert clear pixels THROUGH the table's box, not merely unrelated image corners.
                insideBounds[index] = fixture.proxies[0].bounds.IntersectRay(ray);
            }

            var table = NewCheck("table-top-legs-and-feet-painted", .96f);
            var bottle = NewCheck("bottle-painted", .95f);
            var holes = NewCheck("open-space-between-table-legs-clear", .99f);
            var wall = NewCheck("wall-behind-bounds-clear", .99f);
            var occluder = NewCheck("foreground-occluder-clear", .99f);
            var unavailable = NewCheck("no-depth-draws-no-volume", 1f);
            var invalid = NewCheck("invalid-depth-draws-no-volume", 1f);
            for (var y = Margin; y < Height - Margin; y++)
            for (var x = Margin; x < Width - Margin; x++)
            {
                var index = y * Width + x;
                Sample(unavailable, Difference(baseline[index], noDepth[index]) <= PixelTolerance);
                Sample(invalid, Difference(baseline[index], invalidDepth[index]) <= PixelTolerance);
                if (!Interior(labels, x, y)) continue;
                var unchanged = Difference(baseline[index], painted[index]) <= PixelTolerance;
                var blue = (painted[index].b - baseline[index].b) / 255f >= BlueThreshold;
                switch (labels[index])
                {
                    case Surface.Table: Sample(table, blue); break;
                    case Surface.Bottle: Sample(bottle, blue); break;
                    case Surface.Occluder:
                        if (insideBounds[index]) Sample(occluder, unchanged);
                        break;
                    case Surface.Wall:
                        if (insideBounds[index]) { Sample(wall, unchanged); Sample(holes, unchanged); }
                        break;
                    case Surface.Floor:
                        if (insideBounds[index]) Sample(holes, unchanged);
                        break;
                }
            }
            foreach (var check in new[] { table, bottle, holes, wall, occluder, unavailable, invalid })
            {
                check.matchingFraction = check.sampledPixels == 0 ? 0 : (float)check.matchingPixels / check.sampledPixels;
                check.passed = check.sampledPixels >= 20 && check.matchingFraction >= check.requiredFraction;
                result.checks.Add(check);
            }
        }

        private static bool Interior(Surface[] labels, int x, int y)
        {
            var expected = labels[y * Width + x];
            for (var dy = -Margin; dy <= Margin; dy++)
            for (var dx = -Margin; dx <= Margin; dx++)
                if (labels[(y + dy) * Width + x + dx] != expected) return false;
            return true;
        }

        private static Check NewCheck(string name, float required) => new Check { name = name, requiredFraction = required };
        private static void Sample(Check check, bool matches) { check.sampledPixels++; if (matches) check.matchingPixels++; }
        private static float Difference(Color32 a, Color32 b) =>
            Mathf.Max(Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Abs(a.g - b.g)),
                Mathf.Max(Mathf.Abs(a.b - b.b), Mathf.Abs(a.a - b.a))) / 255f;
        private static void Save(Texture2D texture, string directory, string name) =>
            File.WriteAllBytes(Path.Combine(directory, name), texture.EncodeToPNG());

        private static void SaveComparison(Texture2D baseline, Texture2D painted, string directory, string name)
        {
            var comparison = new Texture2D(Width * 2, Height, TextureFormat.RGBA32, false);
            comparison.SetPixels(0, 0, Width, Height, baseline.GetPixels());
            comparison.SetPixels(Width, 0, Width, Height, painted.GetPixels());
            comparison.Apply(false, false);
            Save(comparison, directory, name);
            Object.DestroyImmediate(comparison);
        }

        private static void SetDepthKeywords(Material material, bool enabled)
        {
            SetGlobalKeyword("SOFT_OCCLUSION", false);
            SetGlobalKeyword("HARD_OCCLUSION", enabled);
            material.DisableKeyword("SOFT_OCCLUSION");
            if (enabled) material.EnableKeyword("HARD_OCCLUSION");
            else material.DisableKeyword("HARD_OCCLUSION");
        }

        private static void SetGlobalKeyword(string keyword, bool enabled)
        {
            if (enabled) Shader.EnableKeyword(keyword);
            else Shader.DisableKeyword(keyword);
        }

        private static void CheckShader(Shader shader)
        {
            if (!ShaderUtil.ShaderHasError(shader)) return;
            var messages = new List<string>();
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                messages.Add($"{message.severity}: {message.message} ({message.file}:{message.line})");
            throw new InvalidOperationException("Production SurfaceGlow shader compilation failed: " + string.Join("\n", messages));
        }
    }
}
