using System.Collections;
using CutOnce.Vision;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Proves the Editor preview really produces model detections from the photo.</summary>
public static class PreviewTest
{
    private static int _detections;
    private static string _first = "";
    private static bool _started;

    public static void Run()
    {
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
        EditorSceneManager.OpenScene(MakeScannerScene.Path);   // clean scene: no CutOnceApp spam
        EditorApplication.update += Tick;
        EditorApplication.EnterPlaymode();
    }

    private static void Tick()
    {
        if (!EditorApplication.isPlaying || _started) return;
        _started = true;
        EditorApplication.update -= Tick;
        new GameObject("[PreviewTest]").AddComponent<Runner>().StartCoroutine(Body());
    }

    private class Runner : MonoBehaviour { }

    private static IEnumerator Body()
    {
        // Drive the test with a real photo so this proves detection, not just that code runs.
        var test = "/tmp/vision_testphoto.jpg";
        if (System.IO.File.Exists(test)) EditorPrefs.SetString(EditorPreview.PhotoPrefKey, test);
        EditorPreview.ForceStart();

        RoomScanner scanner = null;
        for (var i = 0; i < 300 && scanner == null; i++) { scanner = Object.FindFirstObjectByType<RoomScanner>(); yield return null; }

        var deadline = Time.realtimeSinceStartup + 120f;
        while (Time.realtimeSinceStartup < deadline && _detections == 0)
        {
            if (scanner != null && scanner.Tracker != null && scanner.Tracker.Objects.Count > 0)
            {
                _detections = scanner.Tracker.Objects.Count;
                foreach (var o in scanner.Tracker.Objects) { _first = $"{o.className} @ {o.smoothedWorldPosition}"; break; }
            }
            yield return null;
        }

        System.Console.WriteLine("===== EDITOR PREVIEW TEST =====");
        System.Console.WriteLine($"scanner present: {scanner != null}");
        System.Console.WriteLine($"model loaded:    {scanner?.Detector?.ModelLoaded}");
        System.Console.WriteLine($"raw candidates:  {scanner?.Detector?.LastRawDetections}");
        System.Console.WriteLine($"tracked objects: {_detections}");
        System.Console.WriteLine($"first:           {_first}");
        System.Console.WriteLine($"detector error:  '{scanner?.Detector?.LastError}'");
        System.Console.WriteLine("===== END EDITOR PREVIEW TEST =====");
        EditorApplication.Exit(_detections > 0 ? 0 : 1);
    }
}
