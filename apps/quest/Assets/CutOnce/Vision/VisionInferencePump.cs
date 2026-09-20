using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>Unparented player driver: scanner/scene deactivation cannot stop outstanding work.</summary>
    public sealed class VisionInferencePump : MonoBehaviour
    {
        private static VisionInferencePump _instance;

        internal static void EnsureRunning()
        {
            if (_instance != null) return;
            var owner = new GameObject("Vision inference lifetime (not scene content)")
            { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(owner);
            _instance = owner.AddComponent<VisionInferencePump>();
        }

        private void Update() => VisionInferenceLifetime.TickPlayer();
        private void OnDestroy() { if (_instance == this) _instance = null; }
    }
}
