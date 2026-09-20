using UnityEditor;

namespace CutOnce.Vision.Editor
{
    /// <summary>Editor-only adapter; the same retained operations also serve batch photo proofs.</summary>
    [InitializeOnLoad]
    internal sealed class EditorVisionInferenceDriver : IVisionInferenceDriver
    {
        static EditorVisionInferenceDriver()
        {
            VisionInferenceLifetime.RegisterEditorDriver(new EditorVisionInferenceDriver());
            EditorApplication.update += VisionInferenceLifetime.TickEditor;
            EditorApplication.playModeStateChanged += state =>
            {
                // OnApplicationQuit also means stopping Play mode, not just process exit.
                // Hand unfinished operations to Editor updates while the player pump is torn down.
                if (state == PlayModeStateChange.ExitingPlayMode)
                    VisionInferenceLifetime.SetEditorPlayModeExiting(true);
                else if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.EnteredPlayMode)
                    VisionInferenceLifetime.SetEditorPlayModeExiting(false);
            };
        }

        public void EnsureRunning() => EditorApplication.QueuePlayerLoopUpdate();
    }
}
