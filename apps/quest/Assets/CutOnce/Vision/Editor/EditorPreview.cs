using System.Collections;
using System.IO;
using CutOnce.Vision;
using Meta.XR.MRUtilityKit;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Makes Play do something useful on a Mac, where there is no passthrough camera and no depth.
///
/// EDITOR ONLY — this assembly is not in the player build. It does three things the device does
/// for real, so you can see the pipeline work without a headset:
///   1. loads one of MRUK's bundled scanned rooms, so the blue room glow has geometry to sit on
///   2. feeds a real PHOTO into the real YoloDetector, so the labels are genuine model output
///   3. places hits at a FIXED DISTANCE, because there is no depth sensor here
///
/// Point 3 is the one to remember: positions in the Editor are fake and it says so on every
/// placement. On the headset that comes from EnvironmentRaycastManager and is real. The NAMES are
/// real in both — they come from the model either way.
/// </summary>
public static class EditorPreview
{
    private const string DefaultPhoto = "Assets/CutOnce/Vision/Editor/PreviewPhoto.jpg";

    /// <summary>
    /// Point this at a photo of your own desk to see real labels: Unity menu
    /// Cut Once > Vision > Pick preview photo, or just overwrite PreviewPhoto.jpg.
    /// The shipped default is a synthetic render with no real-world objects in it, so the model
    /// correctly finds nothing in it — that is the model working, not failing.
    /// </summary>
    public const string PhotoPrefKey = "CutOnce.Vision.PreviewPhoto";

    private static string PhotoPath => EditorPrefs.GetString(PhotoPrefKey, DefaultPhoto);

    [MenuItem("Cut Once/Vision/Pick preview photo...")]
    private static void PickPhoto()
    {
        var picked = EditorUtility.OpenFilePanel("Photo to run detection on", "", "jpg,jpeg,png");
        if (string.IsNullOrEmpty(picked)) return;
        EditorPrefs.SetString(PhotoPrefKey, picked);
        Debug.Log($"[Vision][EDITOR PREVIEW] preview photo set to {picked}. Press Play.");
    }
    private const string MockRoomGlob = "Core/Rooms/Json/MeshLivingRoom2.json";
    private const float FakeDistance = 2.0f;
    private const float Interval = 2f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Boot()
    {
        // Skip in batch mode so it cannot perturb the verification suite; tests call ForceStart().
        if (Application.isBatchMode) return;
        // Only in the scene it was built for. It loads a mock room over MRUK and runs the model every couple of
        // seconds, which is not what someone pressing Play on Main.unity (or a PlayMode test) asked for.
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "ObjectScanner") return;
        ForceStart();
    }

    /// <summary>Start the preview explicitly (used by the headless test).</summary>
    public static GameObject ForceStart()
    {
        var go = new GameObject("[Vision EDITOR PREVIEW]");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<Driver>();
        return go;
    }

    private class Driver : MonoBehaviour
    {
        private RoomScanner _scanner;
        private Texture2D _photo;
        private bool _ranOnce;
        private int _reported;

        private IEnumerator Start()
        {
            Debug.Log("[Vision][EDITOR PREVIEW] starting — no camera and no depth on a Mac, so this " +
                      "feeds a photo through the real model and places results at a fixed distance.");

            // Wait for the real bootstrap to install the scanner.
            for (var i = 0; i < 120 && _scanner == null; i++)
            {
                _scanner = FindFirstObjectByType<RoomScanner>();
                yield return null;
            }
            if (_scanner == null) { Debug.LogError("[Vision][EDITOR PREVIEW] RoomScanner never installed."); yield break; }

            LoadMockRoom();

            _photo = new Texture2D(2, 2);
            var path = PhotoPath;
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (bytes == null || !_photo.LoadImage(bytes))
            {
                Debug.LogError($"[Vision][EDITOR PREVIEW] could not load {path}");
                yield break;
            }
            Debug.Log($"[Vision][EDITOR PREVIEW] photo loaded: {_photo.width}x{_photo.height} from {path}");

            // Show whatever the model ranks highest: this is a look-at-it preview, not a precision test.
            _scanner.Detector.scoreThreshold = 0.10f;
            _scanner.minConfidence = 0f;
            _scanner.Tracker.hitsBeforeVisible = 1;
            _scanner.Tracker.keepAliveSeconds = 60f;

            _scanner.Detector.OnDetections += Place;

            while (true)
            {
                if (_scanner.Detector.ModelLoaded)
                {
                    _ranOnce = true;
                    var cam = Camera.main;
                    var pose = cam != null ? new Pose(cam.transform.position, cam.transform.rotation) : new Pose();
                    yield return _scanner.Detector.DetectOnce(_photo, pose);
                }
                if (_ranOnce && _scanner.Tracker.Objects.Count == 0 && _reported++ == 1)
                {
                    Debug.LogWarning($"[Vision][EDITOR PREVIEW] the model found none of its 80 classes in this photo " +
                                     $"({_scanner.Detector.LastRawDetections} candidates scored). That is correct if the photo " +
                                     $"has no laptop/chair/bottle/etc in it. Use Cut Once > Vision > Pick preview photo " +
                                     $"and choose a photo of your desk.");
                }
                yield return new WaitForSeconds(Interval);
            }
        }

        /// <summary>Give MRUK a real scanned room from its own package so the glow has something to cling to.</summary>
        private void LoadMockRoom()
        {
            var mruk = FindFirstObjectByType<MRUK>();
            if (mruk == null) { Debug.LogWarning("[Vision][EDITOR PREVIEW] no MRUK in scene; skipping room glow."); return; }

            var pkg = Path.Combine(Application.dataPath, "../Library/PackageCache");
            var dir = Directory.Exists(pkg) ? Directory.GetDirectories(pkg, "com.meta.xr.mrutilitykit*") : new string[0];
            if (dir.Length == 0) { Debug.LogWarning("[Vision][EDITOR PREVIEW] MRUK package not found; skipping room glow."); return; }

            var json = Path.Combine(dir[0], MockRoomGlob);
            if (!File.Exists(json)) { Debug.LogWarning("[Vision][EDITOR PREVIEW] mock room json missing; skipping room glow."); return; }

            mruk.LoadSceneFromJsonString(File.ReadAllText(json));
            Debug.Log("[Vision][EDITOR PREVIEW] loaded MRUK mock room (MeshLivingRoom2) so the blue glow is visible.");
        }

        /// <summary>Stand in for depth: straight down the detection ray at a fixed distance.</summary>
        private void Place(System.Collections.Generic.List<DetectedObject> detections, Pose cameraPose, Vector2 inputSize)
        {
            var cam = Camera.main;
            if (cam == null) return;

            foreach (var d in detections)
            {
                if (d.confidence < _scanner.minConfidence) continue;

                var viewport = new Vector3(d.centerPixel.x / inputSize.x, 1f - d.centerPixel.y / inputSize.y, FakeDistance);
                var world = cam.ViewportToWorldPoint(viewport);
                _scanner.Tracker.Observe(d, world);
                Debug.Log($"[Vision][EDITOR PREVIEW] Detected: {d.className} {d.confidence:0.00}  (FAKE depth {FakeDistance}m — device uses real depth)");
            }
        }

        private void OnDestroy()
        {
            if (_scanner != null && _scanner.Detector != null) _scanner.Detector.OnDetections -= Place;
        }
    }
}
