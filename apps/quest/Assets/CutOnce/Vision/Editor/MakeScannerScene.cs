using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds a clean scene for the scanner: a camera, a light, nothing else.
///
/// Main.unity carries the whole app, and CutOnceApp.Update() throws every frame without a headset,
/// which buries any scanner log in a wall of NullReferenceExceptions. This scene has none of that,
/// so Play is actually readable — and RoomScannerBootstrap installs the scanner into it by itself.
/// </summary>
public static class MakeScannerScene
{
    public const string Path = "Assets/CutOnce/Scenes/ObjectScanner.unity";

    public static void Run()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        var cam = Camera.main;
        if (cam != null)
        {
            cam.transform.position = new Vector3(0f, 1.6f, 0f);
            cam.transform.rotation = Quaternion.identity;
            cam.clearFlags = CameraClearFlags.SolidColor;
            // Transparent black: on device this is what lets passthrough show through URP.
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.nearClipPlane = 0.05f;
        }

        EditorSceneManager.SaveScene(scene, Path);

        var scenes = new System.Collections.Generic.List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (!scenes.Exists(s => s.path == Path))
        {
            // Appended, never first: Main.unity stays the app's entry point.
            scenes.Add(new EditorBuildSettingsScene(Path, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        System.Console.WriteLine("===== SCANNER SCENE =====");
        System.Console.WriteLine("created: " + Path);
        foreach (var s in EditorBuildSettings.scenes) System.Console.WriteLine($"  build scene: {s.path} (enabled={s.enabled})");
        System.Console.WriteLine("===== END SCANNER SCENE =====");
        EditorApplication.Exit(0);
    }
}
