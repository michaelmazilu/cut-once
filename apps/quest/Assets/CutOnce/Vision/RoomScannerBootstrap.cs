using System.Collections;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>
    /// Starts the scanner on app launch. Nothing to wire into a scene, nothing to press.
    ///
    /// Requests the OS permissions itself and waits for the grant rather than failing silently —
    /// a denied camera permission is the single most likely reason for "the app runs but nothing
    /// happens", so it is reported loudly instead.
    ///
    /// Assets come from Resources because this runs before any scene wiring exists.
    /// Define ROOMSCANNER_NO_AUTOBOOT to opt out and call Install() yourself.
    /// </summary>
    public static class RoomScannerBootstrap
    {
        public const string ModelResource = "yolov9sentis";
        public const string LabelsResource = "SentisYoloClasses";

        public static RoomScanner Instance { get; private set; }

#if !ROOMSCANNER_NO_AUTOBOOT
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoBoot() => Install();
#endif

        public static RoomScanner Install()
        {
            if (Instance != null) return Instance;
            if (Object.FindFirstObjectByType<RoomScanner>() != null) return null;

            // Stand down if the other scanner implementation is in the build. MRUK hands out ONE
            // PassthroughCameraAccess per eye, so two auto-starting scanners would fight over the
            // camera and both lose. Whichever is canonical wins; this one simply goes dormant.
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                var ns = mb.GetType().Namespace;
                if (ns == null || !ns.StartsWith("CutOnce.Scanner")) continue;
                Debug.Log($"[Vision] {mb.GetType().Name} is present; RoomScanner standing down to leave it the camera.");
                return null;
            }

            var model = Resources.Load<Unity.InferenceEngine.ModelAsset>(ModelResource);
            var labels = Resources.Load<TextAsset>(LabelsResource);
            if (model == null || labels == null)
            {
                Debug.LogError($"[Vision] cannot start: Resources/{ModelResource} = {(model == null ? "MISSING" : "ok")}, " +
                               $"Resources/{LabelsResource} = {(labels == null ? "MISSING" : "ok")}. " +
                               "Both must live in a Resources folder to survive the player build.");
                return null;
            }

            // Inactive while we wire it: AddComponent on an ACTIVE object runs Awake synchronously,
            // so the component would read its fields before we had assigned them. Activating last
            // means every Awake sees a fully configured object.
            var go = new GameObject("[RoomScanner]");
            go.SetActive(false);
            Object.DontDestroyOnLoad(go);
            go.AddComponent<PermissionGate>();

            var scanner = go.AddComponent<RoomScanner>();
            scanner.modelAsset = model;
            scanner.labelsAsset = labels;
            go.AddComponent<RivalWatch>();
            go.SetActive(true);
            Instance = scanner;

            Debug.Log("[Vision] RoomScanner installed; requesting permissions.");
            return scanner;
        }

        /// <summary>
        /// The one-shot check in Install() is not enough: the order in which
        /// RuntimeInitializeOnLoadMethod hooks run across classes is undefined, so the rival
        /// scanner may not exist yet when we look. Keep looking for a few frames, and if one turns
        /// up, delete ourselves — only one pipeline may ever own PassthroughCameraAccess.
        /// </summary>
        private class RivalWatch : MonoBehaviour
        {
            private const int FramesToWatch = 20;

            private IEnumerator Start()
            {
                var silencedGaze = false;

                for (var i = 0; i < FramesToWatch; i++)
                {
                    foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                    {
                        var type = mb.GetType();
                        var ns = type.Namespace;

                        // The other scanner implementation: stand down entirely so it keeps the camera.
                        if (ns != null && ns.StartsWith("CutOnce.Scanner"))
                        {
                            Debug.Log($"[Vision] {type.Name} started too; RoomScanner is shutting down so it keeps sole ownership of the camera.");
                            Instance = null;
                            Destroy(gameObject);
                            yield break;
                        }

                        // RoomSense's gaze inspector names things from MRUK labels and bounding-box
                        // size. That is a guess, not recognition, and two naming systems in one
                        // headset is worse than one. Its room GLOW stays — that is what makes
                        // everything in the room light up blue — only the guessed labels go, so
                        // every name the user reads comes from the vision model.
                        if (!silencedGaze && type.FullName == "CutOnce.RoomSense.GazeInspector")
                        {
                            mb.enabled = false;
                            silencedGaze = true;
                            Debug.Log("[Vision] RoomSense GazeInspector disabled: labels come from YOLO only. Room glow is untouched.");
                        }
                    }
                    yield return null;
                }
            }
        }

        /// <summary>
        /// Asks for Scene + camera access on device and reports the outcome. On a platform where
        /// OVRPermissionsRequester is unavailable this is a no-op and the camera simply never starts,
        /// which RoomScanner surfaces as a timeout rather than silence.
        /// </summary>
        private class PermissionGate : MonoBehaviour
        {
            private IEnumerator Start()
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                OVRPermissionsRequester.Request(new[]
                {
                    OVRPermissionsRequester.Permission.Scene,
                    OVRPermissionsRequester.Permission.PassthroughCameraAccess,
                });

                // The OS dialog suspends the app; give it time to come back before we judge.
                var deadline = Time.realtimeSinceStartup + 30f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.PassthroughCameraAccess))
                    {
                        Debug.Log("[Vision] camera permission granted.");
                        yield break;
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                Debug.LogError("[Vision] camera permission NOT granted after 30s — detection cannot run. " +
                               "Grant it in Settings > Apps > Permissions, or reinstall and accept the prompt.");
#else
                yield break;
#endif
            }
        }
    }
}
