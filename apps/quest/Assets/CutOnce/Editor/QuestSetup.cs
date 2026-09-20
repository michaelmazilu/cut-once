using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace CutOnce.QuestTools
{
    /// <summary>
    /// The project's Quest 3 settings, written as code so a Mac and a Windows machine end up identical, and anyone can
    /// put them back with Cut Once > Apply Quest 3 settings. <see cref="QuestChecks"/> verifies the same values, so a
    /// change here needs the matching change there.
    /// </summary>
    public static class QuestSetup
    {
        public const string ProductName = "Cut Once";
        public const string ApplicationId = "com.cutonce.quest";
        public const AndroidSdkVersions MinAndroidSdk = AndroidSdkVersions.AndroidApiLevel32;
        public const int Msaa = 4;
        public const string SettingsFolder = "Assets/CutOnce/Settings";
        public const string UrpAssetPath = SettingsFolder + "/Quest_URP.asset";
        public const string UrpRendererPath = SettingsFolder + "/Quest_Renderer.asset";
        public const string XRSettingsPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
        public const string OpenXRLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        public const string MetaXRFeature = "Meta.XR.MetaXRFeature";
        /// <summary>
        /// Unity's OpenXR Meta package (com.unity.xr.meta-openxr). Its Occlusion feature is what gives the Meta XR SDK's
        /// EnvironmentDepthManager a depth TEXTURE provider under the OpenXR loader: the object highlight's surface paint
        /// needs it. (Depth RAYS — the object locator, Kit's table scan — go through EnvironmentRaycastManager's own
        /// OpenXR provider and work without it.) Occlusion's validation rule requires the Session feature. Matched by
        /// name so this compiles before the package has resolved.
        /// </summary>
        public const string MetaOpenXRSessionFeature = "UnityEngine.XR.OpenXR.Features.Meta.ARSessionFeature";
        public const string MetaOpenXROcclusionFeature = "UnityEngine.XR.OpenXR.Features.Meta.AROcclusionFeature";
        internal const string Quest3ManifestName = "eureka"; // Quest 3's name in the Android manifest

        [MenuItem("Cut Once/Apply Quest 3 settings", priority = 1)]
        static void ApplyFromMenu()
        {
            Apply();
            EditorUtility.DisplayDialog("Cut Once", "Quest 3 settings applied.\n\nNext: Cut Once > Check Quest readiness.", "OK");
        }

        public static void Apply()
        {
            ApplyPlayer();
            ApplyRendering();
            ApplyXR(BuildTargetGroup.Android);
            // Play mode in the Editor uses the Standalone settings, and that is what Meta XR Simulator connects to.
            ApplyXR(BuildTargetGroup.Standalone);
            ApplyMetaProjectConfig();
            AssetDatabase.SaveAssets();
        }

        static void ApplyPlayer()
        {
            PlayerSettings.companyName = ProductName;
            PlayerSettings.productName = ProductName;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = MinAndroidSdk;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.colorSpace = ColorSpace.Linear;
            // Assets/Plugins/Android/AndroidManifest.xml is ours: it declares the headset camera, the microphone and
            // plain http. There is no scripting API for the flag that makes Unity use it, so it is set as the
            // Inspector does — through the serialised settings asset.
            SetPlayerFlag(CustomManifestFlag, true);
            // The laptop server on the venue Wi-Fi is plain http; Android refuses that unless it is allowed here.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            // Otherwise Play mode pauses whenever the simulator window has focus.
            PlayerSettings.runInBackground = true;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
        }

        /// <summary>
        /// Creates an asset folder through the AssetDatabase. A folder made on disk first is not in the database yet,
        /// so a package creating the same folder right after gets "XR 1" instead of "XR".
        /// </summary>
        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)!.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static void ApplyRendering()
        {
            EnsureFolder(SettingsFolder);
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(UrpRendererPath);
            if (renderer == null)
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(renderer, UrpRendererPath);
            }
            renderer.renderingMode = RenderingMode.Forward;
            renderer.depthPrimingMode = DepthPrimingMode.Disabled;
            renderer.postProcessData = null; // no post-processing: it is a full-screen pass per eye
            EditorUtility.SetDirty(renderer);

            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (asset == null)
            {
                asset = UniversalRenderPipelineAsset.Create(renderer);
                AssetDatabase.CreateAsset(asset, UrpAssetPath);
            }
            asset.msaaSampleCount = Msaa;
            // HDR's colour format has no alpha channel, and passthrough shows through where alpha is 0.
            asset.supportsHDR = false;
            asset.supportsCameraDepthTexture = false;
            asset.supportsCameraOpaqueTexture = false;
            asset.renderScale = 1f;
            // The hologram is unlit, so realtime shadows would cost GPU time for nothing anyone sees.
            asset.shadowDistance = 0f;
            EditorUtility.SetDirty(asset);

            GraphicsSettings.defaultRenderPipeline = asset;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
                // Ignored under URP (the asset's MSAA decides), but Meta's Project Setup Tool sets it to 4; agree with it.
                QualitySettings.antiAliasing = Msaa;
                QualitySettings.shadows = UnityEngine.ShadowQuality.Disable;
                QualitySettings.vSyncCount = 0;
            }
            QualitySettings.SetQualityLevel(current, false);
        }

        static void ApplyXR(BuildTargetGroup group)
        {
            var perTarget = XRSettingsPerTarget(create: true);
            if (!perTarget.HasSettingsForBuildTarget(group)) perTarget.CreateDefaultSettingsForBuildTarget(group);
            if (!perTarget.HasManagerSettingsForBuildTarget(group)) perTarget.CreateDefaultManagerSettingsForBuildTarget(group);
            var general = perTarget.SettingsForBuildTarget(group);
            general.InitManagerOnStart = true;
            XRPackageMetadataStore.AssignLoader(general.Manager, OpenXRLoader, group);
            EditorUtility.SetDirty(general);

            FeatureHelpers.RefreshFeatures(group);
            var openxr = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            openxr.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            foreach (var feature in openxr.GetFeatures())
            {
                var fullName = feature.GetType().FullName;
                if (fullName == MetaXRFeature
                    || fullName == MetaOpenXRSessionFeature
                    || fullName == MetaOpenXROcclusionFeature
                    || feature is OculusTouchControllerProfile
                    || feature is MetaQuestTouchPlusControllerProfile)
                    feature.enabled = true;
                if (feature is MetaQuestFeature quest)
                {
                    quest.enabled = true;
                    // AddTargetDevice adds a missing entry but leaves an existing, unticked one unticked.
                    quest.AddTargetDevice(Quest3ManifestName, "Quest 3", true);
                    var serialized = new SerializedObject(quest);
                    var quest3 = TargetDevice(serialized, Quest3ManifestName);
                    if (quest3 != null)
                    {
                        quest3.FindPropertyRelative("enabled").boolValue = true;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
                EditorUtility.SetDirty(feature);
            }
            EditorUtility.SetDirty(openxr);
        }

        /// <summary>The serialized entry for one target device of the Meta Quest feature (its list is not public).</summary>
        internal static SerializedProperty TargetDevice(SerializedObject quest, string manifestName)
        {
            var devices = quest.FindProperty("targetDevices");
            for (int i = 0; devices != null && i < devices.arraySize; i++)
            {
                var device = devices.GetArrayElementAtIndex(i);
                if (device.FindPropertyRelative("manifestName").stringValue == manifestName) return device;
            }
            return null;
        }

        internal static XRGeneralSettingsPerBuildTarget XRSettingsPerTarget(bool create)
        {
            if (EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.settingsKey, out XRGeneralSettingsPerBuildTarget existing) && existing != null)
                return existing;
            if (!create) return null;
            EnsureFolder(Path.GetDirectoryName(XRSettingsPath)!.Replace('\\', '/'));
            var created = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(created, XRSettingsPath);
            EditorBuildSettings.AddConfigObject(XRGeneralSettings.settingsKey, created, true);
            return created;
        }

        static void ApplyMetaProjectConfig()
        {
            var config = OVRProjectConfig.CachedProjectConfig;
            // Quest 3 is the demo headset; a spare from the hardware desk may be a 3S, which has the same cameras.
            config.targetDeviceTypes = new List<OVRProjectConfig.DeviceType> { OVRProjectConfig.DeviceType.Quest3, OVRProjectConfig.DeviceType.Quest3S };
            config.insightPassthroughSupport = OVRProjectConfig.FeatureSupport.Required; // the whole app is mixed reality
            config.isPassthroughCameraAccessEnabled = true; // the copilot's photo: horizonos.permission.HEADSET_CAMERA
            // Depth rays (build mode's scan, placing on a real table): com.oculus.permission.USE_SCENE. Required rather than
            // Supported: both declare the same permission, and Required is what MRUK's and the Depth API's setup rules write,
            // so pnpm quest:setup (Meta's fixes, then ours) and this file agree.
            config.sceneSupport = OVRProjectConfig.FeatureSupport.Required;
            config.anchorSupport = OVRProjectConfig.AnchorSupport.Enabled; // the aligned desk is kept with a spatial anchor
            config.sceneSupport = OVRProjectConfig.FeatureSupport.Required; // room mapping and scene mesh queries
            config.handTrackingSupport = OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            OVRProjectConfig.CommitProjectConfig(config);
        }

        /// <summary>The Player setting that makes Unity build Assets/Plugins/Android/AndroidManifest.xml into the APK.</summary>
        public const string CustomManifestFlag = "useCustomMainManifest";

        static SerializedObject PlayerSettingsAsset()
        {
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            return asset.Length > 0 ? new SerializedObject(asset[0]) : null;
        }

        /// <summary>Reads a Player setting that has no scripting API. Missing (a Unity that renamed it): true, so a check cannot fail on nothing.</summary>
        public static bool GetPlayerFlag(string property)
        {
            var found = PlayerSettingsAsset()?.FindProperty(property);
            return found == null || found.boolValue;
        }

        /// <summary>Writes one, the way the Inspector does.</summary>
        public static void SetPlayerFlag(string property, bool value)
        {
            var settings = PlayerSettingsAsset();
            var found = settings?.FindProperty(property);
            if (found == null) { Debug.LogWarning($"[CutOnce] Player setting {property} not found; set it in Player Settings by hand."); return; }
            found.boolValue = value;
            settings.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }
    }
}
