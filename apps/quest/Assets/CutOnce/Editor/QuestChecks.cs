using System;
using System.Collections.Generic;
using System.Linq;
using CutOnce.Diagnostics;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace CutOnce.QuestTools
{
    /// <summary>
    /// Everything that makes the project behave differently on the Quest than on a laptop, checked in one place:
    /// Android player settings, XR loaders for both the headset and the simulator, the render pipeline, Meta's
    /// project config, and each build scene against <see cref="QuestBudgets"/>. Errors mean "will not work on the
    /// Quest"; warnings mean "will work, but slower or different than you think".
    /// </summary>
    public static class QuestChecks
    {
        public enum Level { Error, Warning }

        [Serializable]
        public struct Finding
        {
            public string level;
            public string area;
            public string message;
            public bool IsError => level == nameof(Level.Error);
            public override string ToString() => $"[{level}] {area}: {message}";
        }

        [MenuItem("Cut Once/Check Quest readiness", priority = 2)]
        static void CheckFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var setup = EditorSceneManager.GetSceneManagerSetup();
            List<Finding> findings;
            try { findings = Run(includeScenes: true); }
            // An empty setup (only an unsaved Untitled scene was open) cannot be restored; the last scan stays open.
            finally { if (setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup); }
            foreach (var f in findings) { if (f.IsError) Debug.LogError(f); else Debug.LogWarning(f); }
            int errors = findings.Count(f => f.IsError);
            EditorUtility.DisplayDialog("Cut Once",
                errors == 0 && findings.Count == 0 ? "Ready for the Quest 3. No problems found."
                : $"{errors} error(s), {findings.Count - errors} warning(s). Details are in the Console.\n\nFor Meta's own list: Meta > Tools > Project Setup Tool.",
                "OK");
        }

        public static List<Finding> Run(bool includeScenes)
        {
            var findings = new List<Finding>();
            CheckPlayer(findings);
            CheckXR(findings);
            CheckRendering(findings);
            CheckMetaConfig(findings);
            if (includeScenes) CheckScenes(findings);
            return findings;
        }

        static void CheckPlayer(List<Finding> f)
        {
            var android = NamedBuildTarget.Android;
            Expect(f, "android", PlayerSettings.GetScriptingBackend(android) == ScriptingImplementation.IL2CPP,
                "Scripting backend must be IL2CPP: the Quest only runs ARM64, which Mono cannot build.");
            Expect(f, "android", PlayerSettings.Android.targetArchitectures == AndroidArchitecture.ARM64,
                "Target architecture must be ARM64 only.");
            Expect(f, "android", PlayerSettings.Android.minSdkVersion >= QuestSetup.MinAndroidSdk,
                $"Minimum API level must be at least {(int)QuestSetup.MinAndroidSdk}, which Horizon OS requires.");
            var apis = PlayerSettings.GetGraphicsAPIs(BuildTarget.Android);
            Expect(f, "android", !PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.Android) && apis.Length == 1 && apis[0] == GraphicsDeviceType.Vulkan,
                "Graphics API must be Vulkan only. Meta dropped OpenGL ES for new features, and the simulator does not run it.");
            Expect(f, "render", PlayerSettings.colorSpace == ColorSpace.Linear,
                "Colour space must be Linear, or every colour on the headset differs from the palette.");
            Expect(f, "network", PlayerSettings.insecureHttpOption == InsecureHttpOption.AlwaysAllowed,
                "Plain http must be allowed, or the headset cannot reach the laptop server over Wi-Fi (the Editor can, so this only fails on the Quest).");
            Expect(f, "android", QuestSetup.GetPlayerFlag(QuestSetup.CustomManifestFlag),
                "Custom Main Manifest must be on, or Assets/Plugins/Android/AndroidManifest.xml — which declares the headset camera and allows the plain http the laptop server speaks — may not reach the APK.");
            Expect(f, "simulator", PlayerSettings.runInBackground,
                "Run In Background must be on, or Play mode pauses whenever the simulator window has focus.");
            Expect(f, "android", PlayerSettings.GetApplicationIdentifier(android) == QuestSetup.ApplicationId,
                $"The package name must be {QuestSetup.ApplicationId}: pnpm quest:install and the demo notes launch it by that name.");        }

        static void CheckXR(List<Finding> f)
        {
            var perTarget = QuestSetup.XRSettingsPerTarget(create: false);
            foreach (var (group, who) in new[] { (BuildTargetGroup.Android, "the Quest (Android)"), (BuildTargetGroup.Standalone, "Play mode and Meta XR Simulator (Standalone)") })
            {
                var general = perTarget != null && perTarget.HasSettingsForBuildTarget(group) ? perTarget.SettingsForBuildTarget(group) : null;
                bool openxr = general != null && general.Manager != null
                    && general.Manager.activeLoaders.Any(l => l != null && l.GetType().FullName == QuestSetup.OpenXRLoader);
                Expect(f, "xr", openxr, $"OpenXR must be the XR loader for {who}.");
                Expect(f, "xr", general != null && general.InitManagerOnStart, $"XR must start with the app for {who}.");

                var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                if (settings == null) { Add(f, Level.Error, "xr", $"OpenXR has no settings for {who}."); continue; }
                Expect(f, "xr", settings.GetFeatures().Any(x => x.enabled && x.GetType().FullName == QuestSetup.MetaXRFeature),
                    $"The Meta XR feature must be on in OpenXR for {who}.");
                // The depth texture (the object highlight's surface paint) exists only through these two. Run
                // `Cut Once > Apply Quest 3 settings` (pnpm quest:setup) after pulling; it turns them on.
                Expect(f, "xr", settings.GetFeatures().Any(x => x.enabled && x.GetType().FullName == QuestSetup.MetaOpenXROcclusionFeature),
                    $"The 'Meta Quest: Occlusion' feature (com.unity.xr.meta-openxr) must be on for {who}: without it there is no depth texture and object highlights fall back to boxes. Run Apply Quest 3 settings.");
                Expect(f, "xr", settings.GetFeatures().Any(x => x.enabled && x.GetType().FullName == QuestSetup.MetaOpenXRSessionFeature),
                    $"The 'Meta Quest: Session' feature must be on for {who}: Occlusion's own validation requires it.");
                Expect(f, "xr", settings.renderMode == OpenXRSettings.RenderMode.SinglePassInstanced,
                    $"Render mode must be Single Pass Instanced for {who}: multi-pass draws everything twice.");
                if (group == BuildTargetGroup.Android)
                {
                    var quest = settings.GetFeatures().OfType<MetaQuestFeature>().FirstOrDefault(x => x.enabled);
                    Expect(f, "xr", quest != null, "The Meta Quest Support feature must be on for Android.");
                    var quest3 = quest != null ? QuestSetup.TargetDevice(new SerializedObject(quest), QuestSetup.Quest3ManifestName) : null;
                    Expect(f, "xr", quest3 != null && quest3.FindPropertyRelative("enabled").boolValue,
                        "Quest 3 must be ticked as a target device in the Meta Quest Support feature.");
                }
            }
        }

        static void CheckRendering(List<Finding> f)
        {
            var urp = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null) { Add(f, Level.Error, "render", "URP must be the render pipeline (Graphics settings). Run Cut Once > Apply Quest 3 settings."); return; }
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                var levelAsset = QualitySettings.GetRenderPipelineAssetAt(i);
                Expect(f, "render", levelAsset == null || levelAsset == urp,
                    $"Quality level '{QualitySettings.names[i]}' uses a different render pipeline asset, so the headset may render differently from the Editor.");
            }
            Expect(f, "render", !urp.supportsHDR, "HDR must be off: its colour format has no alpha, so passthrough disappears behind the hologram.");
            Expect(f, "render", urp.msaaSampleCount == QuestSetup.Msaa, $"MSAA should be {QuestSetup.Msaa}x: thin edge lines shimmer on the headset without it.", Level.Warning);
            Expect(f, "render", !urp.supportsCameraOpaqueTexture && !urp.supportsCameraDepthTexture,
                "The opaque and depth textures cost an extra copy per eye per frame. Turn them off unless a shader needs them.", Level.Warning);
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(QuestSetup.UrpRendererPath);
            Expect(f, "render", renderer != null && renderer.renderingMode == RenderingMode.Forward && renderer.postProcessData == null,
                "The URP renderer must be Forward with post-processing off, as Cut Once > Apply Quest 3 settings sets it.", Level.Warning);
        }

        static void CheckMetaConfig(List<Finding> f)
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            Expect(f, "meta", config.targetDeviceTypes.Contains(OVRProjectConfig.DeviceType.Quest3), "Quest 3 must be a target device (OVRProjectConfig).");
            Expect(f, "meta", config.insightPassthroughSupport != OVRProjectConfig.FeatureSupport.None,
                "Passthrough must be supported, or the headset shows black instead of the room.");
            Expect(f, "meta", config.isPassthroughCameraAccessEnabled,
                "Passthrough camera access must be on, or the copilot gets no photo on the headset.");
            Expect(f, "meta", config.sceneSupport == OVRProjectConfig.FeatureSupport.Required,
                "Scene support must be Required for room mapping. Run Cut Once > Apply Quest 3 settings.");
            Expect(f, "meta", config.anchorSupport == OVRProjectConfig.AnchorSupport.Enabled,
                "Anchor support must be enabled for room-relative placement.");
        }

        static void CheckScenes(List<Finding> f)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToList();
            if (scenes.Count == 0) { Add(f, Level.Error, "scenes", "No scene is in the build. Run Cut Once > Rebuild baseline scene, or add yours in Build Profiles."); return; }
            foreach (var path in scenes) CheckScene(f, EditorSceneManager.OpenScene(path, OpenSceneMode.Single));
        }

        public static void CheckScene(List<Finding> f, Scene scene)
        {
            string area = "scene " + scene.name;
            long triangles = 0;
            int drawCalls = 0, transparent = 0;
            var badShaders = new SortedSet<string>();
            var bigTextures = new SortedSet<string>();
            var roots = scene.GetRootGameObjects();

            foreach (var r in roots.SelectMany(g => g.GetComponentsInChildren<Renderer>(false)))
            {
                if (!r.enabled) continue;
                var mesh = r is SkinnedMeshRenderer skinned ? skinned.sharedMesh
                    : r.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
                if (mesh != null)
                    for (int i = 0; i < mesh.subMeshCount; i++)
                        if (mesh.GetTopology(i) == MeshTopology.Triangles) triangles += mesh.GetIndexCount(i) / 3;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    drawCalls++;
                    if (m.renderQueue >= (int)RenderQueue.Transparent) transparent++;
                    if (IsBuiltInLitShader(m.shader)) badShaders.Add($"{m.name} ({(m.shader != null ? m.shader.name : "no shader")})");
                    foreach (var id in m.GetTexturePropertyNameIDs())
                    {
                        var t = m.GetTexture(id);
                        if (t != null && Mathf.Max(t.width, t.height) > QuestBudgets.MaxTextureSize) bigTextures.Add($"{t.name} ({t.width}x{t.height})");
                    }
                }
            }

            if (drawCalls > QuestBudgets.MaxDrawCalls)
                Add(f, Level.Error, area, $"About {drawCalls} draw calls when everything is in view (budget {QuestBudgets.MaxDrawCalls}). Merge meshes or share materials.");
            if (triangles > QuestBudgets.MaxTriangles)
                Add(f, Level.Error, area, $"{triangles:N0} triangles (budget {QuestBudgets.MaxTriangles:N0}). Simplify the meshes.");
            if (transparent > QuestBudgets.MaxTransparentRenderers)
                Add(f, Level.Warning, area, $"{transparent} see-through surfaces (budget {QuestBudgets.MaxTransparentRenderers}). Each one is another pass over its pixels.");
            foreach (var s in badShaders)
                Add(f, Level.Error, area, $"Material {s} uses a built-in lit shader, which draws pink under URP on the headset. Use a URP shader.");
            foreach (var t in bigTextures)
                Add(f, Level.Warning, area, $"Texture {t} is larger than {QuestBudgets.MaxTextureSize}px; the Quest has little memory bandwidth to spare.");

            foreach (var light in roots.SelectMany(g => g.GetComponentsInChildren<Light>(false)))
                if (light.enabled && light.shadows != LightShadows.None && light.lightmapBakeType != LightmapBakeType.Baked)
                    Add(f, Level.Warning, area, $"Light '{light.name}' casts realtime shadows. The hologram is unlit, so they cost GPU time for nothing.");
            foreach (var volume in roots.SelectMany(g => g.GetComponentsInChildren<Volume>(false)))
                if (volume.enabled)
                    Add(f, Level.Warning, area, $"Post-processing volume '{volume.name}': a full-screen pass per eye. The renderer has post-processing off, so it does nothing but cost time.");

            bool hasRig = roots.Any(g => g.GetComponentInChildren<OVRManager>(true) != null);
            Expect(f, area, hasRig, "No OVRManager (OVRCameraRig) in the scene: it will not start XR or passthrough on the headset.");
            bool hasProbe = roots.Any(g => g.GetComponentInChildren<BudgetProbe>(true) != null);
            Expect(f, area, hasProbe, "No BudgetProbe in the scene: runtime-loaded plans are not measured against the Quest budget.", Level.Warning);
        }

        static readonly string[] BuiltInLit = { "Standard", "Standard (Specular setup)", "Autodesk Interactive", "Hidden/InternalErrorShader" };

        static bool IsBuiltInLitShader(Shader shader) =>
            shader == null || BuiltInLit.Contains(shader.name) || shader.name.StartsWith("Legacy Shaders/") || shader.name.StartsWith("Mobile/");

        static void Expect(List<Finding> f, string area, bool ok, string message, Level level = Level.Error)
        {
            if (!ok) Add(f, level, area, message);
        }

        static void Add(List<Finding> f, Level level, string area, string message) =>
            f.Add(new Finding { level = level.ToString(), area = area, message = message });
    }
}
