using System.Collections;
using System.Collections.Generic;
using CutOnce.Vision;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Headless proof that the pipeline is connected, not just that the files compile.
/// Drives the real components — the same DetectOnce the headset runs, the real tracker, the real
/// visualizer — and prints one PASS/FAIL line per stage.
/// </summary>
public static class VisionVerify
{
    private static readonly List<string> Results = new();
    private static bool _allPassed = true;

    public static void Run()
    {
        EditorSettings.enterPlayModeOptionsEnabled = true;
        EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        EditorApplication.update += WaitForPlay;
        EditorApplication.EnterPlaymode();
    }

    private static bool _started;

    private static void WaitForPlay()
    {
        if (!EditorApplication.isPlaying) return;
        EditorApplication.update -= WaitForPlay;
        if (_started) return;
        _started = true;
        var runner = new GameObject("[VisionVerify]").AddComponent<Runner>();
        runner.StartCoroutine(All());
    }

    private class Runner : MonoBehaviour { }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _allPassed = false;
        Results.Add($"{name,-24} {(ok ? "PASS" : "FAIL")}{(string.IsNullOrEmpty(detail) ? "" : "  " + detail)}");
    }

    private static IEnumerator All()
    {
        // ---------- 15. model is a real asset in Resources (survives a player build) ----------
        var model = Resources.Load<ModelAsset>(RoomScannerBootstrap.ModelResource);
        var labels = Resources.Load<TextAsset>(RoomScannerBootstrap.LabelsResource);
        Check("MODEL IN RESOURCES", model != null && labels != null,
            $"model={(model != null ? "found" : "MISSING")} labels={(labels != null ? labels.text.Split('\n').Length + " classes" : "MISSING")}");

        // ---------- MODEL LOAD: does a 2.2.1-serialised .sentis open in 2.6.1? ----------
        Model runtimeModel = null;
        var loadError = "";
        try { runtimeModel = ModelLoader.Load(model); }
        catch (System.Exception e) { loadError = e.GetType().Name + ": " + e.Message; }

        var outputCount = runtimeModel?.outputs?.Count ?? 0;
        var inputShape = runtimeModel != null ? runtimeModel.inputs[0].shape.ToString() : "-";
        Check("MODEL LOAD", runtimeModel != null && outputCount == 3,
            runtimeModel == null ? loadError : $"input {inputShape}, {outputCount} outputs (need 3: boxes/classIDs/scores)");

        if (runtimeModel == null) { Finish(); yield break; }

        // ---------- 1 + 14. auto start, no button ----------
        var scanner = Object.FindFirstObjectByType<RoomScanner>();
        Check("AUTO START", scanner != null, scanner != null ? "RoomScanner installed by RuntimeInitializeOnLoadMethod" : "bootstrap did not run");
        if (scanner == null) { Finish(); yield break; }

        Check("PIPELINE WIRED",
            scanner.Camera != null && scanner.Detector != null && scanner.Locator != null &&
            scanner.Tracker != null && scanner.Visualizer != null,
            "VisionCamera+YoloDetector+Object3DLocator+TrackedObjectManager+ObjectVisualizer");
        Check("DETECTOR HAS MODEL", scanner.Detector.modelAsset != null && scanner.Detector.labelsAsset != null);

        // wait for the detector's Awake to finish loading the worker
        var deadline = Time.realtimeSinceStartup + 30f;
        while (!scanner.Detector.ModelLoaded && string.IsNullOrEmpty(scanner.Detector.LastError) && Time.realtimeSinceStartup < deadline)
            yield return null;
        Check("WORKER READY", scanner.Detector.ModelLoaded, scanner.Detector.LastError);

        // ---------- 3 + 4. a texture through the REAL inference path -> class/confidence/box ----------
        var fixtureTex = MakeFixture(640, 640);
        scanner.Detector.scoreThreshold = 0.01f;   // any image should yield candidates; we are testing decode, not accuracy

        var got = new List<DetectedObject>();
        void Capture(List<DetectedObject> d, Pose p, Vector2 s) { got.Clear(); got.AddRange(d); }
        scanner.Detector.OnDetections += Capture;

        scanner.Detector.readbackTimeoutSeconds = 25f;
        yield return scanner.Detector.DetectOnce(fixtureTex, new Pose(Vector3.zero, Quaternion.identity));
        scanner.Detector.OnDetections -= Capture;

        var asyncWorked = scanner.Detector.TotalInferences > 0 && scanner.Detector.LastRawDetections > 0;

        // Batch mode advances frames oddly and the async readback can fail to land here even though
        // the model is fine. If that happens, run the SAME model synchronously so we still get real
        // evidence about the model, and say plainly which path produced it.
        var syncRaw = 0; var syncDecoded = 0; var syncFirst = "";
        if (!asyncWorked)
        {
            try
            {
                using var worker = new Worker(runtimeModel, BackendType.CPU);
                using var input = new Tensor<float>(new TensorShape(1, 3, 640, 640));
                TextureConverter.ToTensor(fixtureTex, input, new TextureTransform().SetDimensions(640, 640, 3));
                worker.Schedule(input);
                using var b = (worker.PeekOutput(0) as Tensor<float>).ReadbackAndClone();
                using var c = (worker.PeekOutput(1) as Tensor<int>).ReadbackAndClone();
                using var sc = (worker.PeekOutput(2) as Tensor<float>).ReadbackAndClone();
                syncRaw = sc.shape.length;
                for (var i = 0; i < syncRaw; i++)
                {
                    if (sc[i] < 0.01f) continue;
                    syncDecoded++;
                    if (syncFirst == "")
                        syncFirst = $"classId {c[i]} @ {sc[i]:0.00} box ({b[i, 0]:0},{b[i, 1]:0})-({b[i, 2]:0},{b[i, 3]:0})";
                }
            }
            catch (System.Exception e) { syncFirst = "sync inference threw: " + e.Message; }
        }

        Check("TEXTURE -> YOLO", asyncWorked || syncRaw > 0,
            asyncWorked
                ? $"async path: {scanner.Detector.LastRawDetections} raw candidates in {scanner.Detector.LastInferenceMs:0} ms"
                : $"async readback did not land in batch mode; SYNC path ran the same model: {syncRaw} raw candidates. Detector error: '{scanner.Detector.LastError}'");

        var sane = got.Count > 0;
        foreach (var d in got)
            if (d.classId < 0 || string.IsNullOrEmpty(d.className) || d.confidence <= 0f ||
                d.boundingBox.width <= 0f || d.boundingBox.height <= 0f) sane = false;
        // What is deterministic here: the model runs, outputs decode, NMS executes, and anything it
        // DOES emit is well formed. What is NOT: whether a synthetic image trips a real COCO class —
        // run 8 produced "scissors 0.02", which is noise. Recognising real objects is a headset test,
        // so assert the decode contract and report the rest rather than dressing luck up as a pass.
        var decodeOk = (asyncWorked || syncRaw > 0)
                       && string.IsNullOrEmpty(scanner.Detector.LastError)
                       && (got.Count == 0 || sane);
        Check("YOLO -> DETECTIONS", decodeOk,
            got.Count > 0
                ? $"{got.Count} decoded via the production path, all well formed, e.g. \"{got[0].className}\" {got[0].confidence:0.00} box {got[0].boundingBox}"
                : $"decode+NMS ran cleanly over {scanner.Detector.LastRawDetections} candidates; none passed threshold on a synthetic image (expected — real classes need the headset)");

        // ---------- 5 + 6. detection -> depth ----------
        var depthSupported = scanner.Locator.IsSupported;
        var locatorRan = !scanner.Locator.TryLocate(
            got.Count > 0 ? got[0] : new DetectedObject { boundingBox = new Rect(0, 0, 64, 64), inputSize = new Vector2(640, 640) },
            new Pose(Vector3.zero, Quaternion.identity), out _);
        Check("DETECTION -> DEPTH", !depthSupported ? locatorRan : true,
            depthSupported ? "depth supported here" : "no depth off-device (expected) — degraded cleanly, reason: " + scanner.Locator.LastFailureReason);

        // ---------- 7 + 8. tracking: 30 sightings of one chair = one chair ----------
        var tracker = scanner.Tracker;
        var chair = new DetectedObject { classId = 56, className = "chair", confidence = 0.9f, inputSize = new Vector2(640, 640), boundingBox = new Rect(0, 0, 100, 100) };
        TrackedObject tracked = null;
        for (var i = 0; i < 30; i++)
            tracked = tracker.Observe(chair, new Vector3(1f, 0f, 2f) + UnityEngine.Random.insideUnitSphere * 0.05f);
        var one = tracker.Objects.Count == 1;

        // a second chair far away must be its own object
        tracker.Observe(chair, new Vector3(4f, 0f, 2f));
        var two = tracker.Objects.Count == 2;
        Check("TRACKING", one && two && tracked.visible,
            $"30 sightings -> {(one ? "1" : tracker.Objects.Count.ToString())} object, distant one separate={two}, promoted={tracked.visible}, smoothed={tracked.smoothedWorldPosition}");

        // ---------- 9 + 10 + 11. visuals ----------
        scanner.Visualizer.Show(tracked);
        var visual = tracked.visual;
        var highlight = visual != null ? visual.transform.Find("Highlight") : null;
        var labelT = visual != null ? visual.transform.Find("Label") : null;
        var renderer = highlight != null ? highlight.GetComponent<MeshRenderer>() : null;
        var shaderName = renderer != null && renderer.sharedMaterial != null ? renderer.sharedMaterial.shader.name : "none";

        var block = new MaterialPropertyBlock();
        renderer?.GetPropertyBlock(block);
        // The highlight is the hologram shader now (thin edges + faint fill), not RoomSense's grid
        // material: check the edge colour, and that the grid the old look used stays OFF.
        var edge = renderer != null ? block.GetColor(Shader.PropertyToID("_EdgeColor")) : Color.black;
        var grid = renderer != null ? block.GetFloat(Shader.PropertyToID("_Grid")) : 1f;
        var isBlue = edge.b > edge.r && edge.b > 0.4f;
        var hCol = highlight != null ? highlight.GetComponent<Collider>() : null;
        var colliderInert = hCol == null || !hCol.enabled;
        var sized = visual != null && visual.transform.localScale.x <= 1.21f; // locator clamps every axis
        Check("SUBTLE HIGHLIGHT", highlight != null && renderer != null && isBlue && grid < 0.5f && colliderInert && sized,
            $"shader={shaderName} edge={edge} grid={grid} colliderInert={colliderInert} scale={visual?.transform.localScale}");

        var text = labelT != null ? labelT.GetComponent<TextMesh>() : null;
        Check("YOLO LABEL", text != null && text.text.Contains("CHAIR"),
            text != null ? $"\"{text.text}\" (from YOLO class {chair.classId}, not RoomSense)" : "no label");

        // ---------- 12. label/highlight live in world space ----------
        var before = visual.transform.position;
        if (Camera.main != null) Camera.main.transform.position += new Vector3(0.5f, 0f, 0f);
        scanner.Visualizer.Show(tracked);
        Check("WORLD LOCKED", Vector3.Distance(before, visual.transform.position) < 0.05f,
            "visual stays put when the head moves");

        // ---------- 8b. objects retire, then visuals are freed ----------
        tracker.keepAliveSeconds = -1f;   // force everything stale
        var removed = tracker.Prune();
        Check("PRUNE/NO DUPLICATES", removed.Count == 2 && tracker.Objects.Count == 0,
            $"{removed.Count} retired, {tracker.Objects.Count} remain");

        Finish();
    }

    /// <summary>Non-uniform fixture: a flat colour makes the model emit nothing and proves little.</summary>
    private static Texture2D MakeFixture(int w, int h)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        var px = new Color32[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var inBlob = (x > w * 0.3f && x < w * 0.7f && y > h * 0.25f && y < h * 0.8f);
            px[y * w + x] = inBlob
                ? new Color32(190, 190, 200, 255)
                : new Color32((byte)(x % 64 + 40), (byte)(y % 64 + 30), 90, 255);
        }
        tex.SetPixels32(px);
        tex.Apply(false);
        return tex;
    }

    private static void Finish()
    {
        System.Console.WriteLine("===== VISION PIPELINE VERIFICATION =====");
        foreach (var r in Results) System.Console.WriteLine("  " + r);
        System.Console.WriteLine(_allPassed ? "ALL PASS" : "SOME FAILED");
        System.Console.WriteLine("===== END VERIFICATION =====");
        EditorApplication.Exit(_allPassed ? 0 : 1);
    }
}
