using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>Editor and player adapters wake the same retained vision-operation list.</summary>
    public interface IVisionInferenceDriver { void EnsureRunning(); }

    /// <summary>
    /// Lifetime owner for vision inference only. Scanner GameObject/parent deactivation must not
    /// stop a submitted GPU readback, release its input lease, or start a second scanner loop.
    /// </summary>
    public static class VisionInferenceLifetime
    {
        private static readonly List<VisionInferenceOperation> Operations = new List<VisionInferenceOperation>(4);
        private static IVisionInferenceDriver _editorDriver;
        private static bool _editorTakingOver;
        public static int PendingCount => Operations.Count;
        public static bool HasEditorDriver => _editorDriver != null;

        public static void RegisterEditorDriver(IVisionInferenceDriver driver) => _editorDriver = driver;
        public static void SetEditorPlayModeExiting(bool exiting)
        {
            _editorTakingOver = exiting;
            // With domain reload disabled, an Editor retirement may still be pending when the
            // next Play session starts. Recreate its independent player driver even in an empty scene.
            if (!exiting && Application.isPlaying && Operations.Count > 0) VisionInferencePump.EnsureRunning();
        }

        public static VisionInferenceOperation Run(IEnumerator routine, Action<Exception> onError = null)
        {
            // Establish a usable driver BEFORE retaining a body whose input the caller may
            // release if scheduling throws. A failed submission must never run later.
            if (Application.isPlaying && !_editorTakingOver) VisionInferencePump.EnsureRunning();
            else if (_editorDriver != null) _editorDriver.EnsureRunning();
            else throw new InvalidOperationException("The Editor vision inference driver has not initialized.");
            var operation = new VisionInferenceOperation(routine, onError);
            Operations.Add(operation);
            return operation;
        }

        public static void TickEditor()
        {
            if (Application.isPlaying && !_editorTakingOver) return;
            Tick();
            if (Operations.Count > 0) _editorDriver?.EnsureRunning();
        }

        internal static void TickPlayer()
        {
            if (Application.isPlaying && !_editorTakingOver) Tick();
        }

        private static void Tick()
        {
            // Only operations present at update entry run; newly submitted work starts next update.
            for (var index = Operations.Count - 1; index >= 0; --index)
            {
                var operation = Operations[index];
                operation.Tick();
                if (operation.IsComplete) Operations.RemoveAt(index);
            }
        }
    }
}
