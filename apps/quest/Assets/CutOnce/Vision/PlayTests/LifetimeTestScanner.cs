using System.Collections;
using UnityEngine;

namespace CutOnce.Vision.PlayTests
{
    /// <summary>Controlled fake backend for actual GameObject lifecycle tests, not camera/GPU proof.</summary>
    public sealed class LifetimeTestScanner : MonoBehaviour
    {
        public sealed class State
        {
            public bool Ready, CaptureDone, InferenceDone, Retire, CapturePending, LeaseHeld, Disposed;
            public int Generation, LoopStarts, Captures, Inferences, Publications, Releases;
            public VisionInferenceOperation Loop, Body, Retirement;
        }
        public readonly State Data = new State();

        private void Start()
        {
            ++Data.LoopStarts;
            Data.Loop = VisionInferenceLifetime.Run(Loop());
        }

        private IEnumerator Loop()
        {
            var state = Data;
            while (!state.Retire)
            {
                if (!isActiveAndEnabled || !state.Ready) { yield return null; continue; }
                var generation = state.Generation;
                ++state.Captures;
                state.CapturePending = true;
                while (!state.CaptureDone) yield return null;
                state.CapturePending = false;
                if (state.Retire) yield break;
                if (generation != state.Generation || !isActiveAndEnabled) { yield return null; continue; }
                state.LeaseHeld = true;
                try
                {
                    state.Body = VisionInferenceLifetime.Run(Infer(state, generation));
                    while (!state.Body.IsComplete) yield return null;
                }
                finally { state.LeaseHeld = false; ++state.Releases; }
                yield return null;
            }
        }

        private static IEnumerator Infer(State state, int generation)
        {
            ++state.Inferences;
            while (!state.InferenceDone) yield return null;
            if (!state.Retire && generation == state.Generation) ++state.Publications;
        }

        private void OnDisable() => ++Data.Generation;

        private void OnDestroy()
        {
            Data.Retire = true;
            ++Data.Generation;
            Data.Retirement = VisionInferenceLifetime.Run(Retire(Data));
        }

        private static IEnumerator Retire(State state)
        {
            while ((state.Loop != null && !state.Loop.IsComplete) || (state.Body != null && !state.Body.IsComplete)) yield return null;
            state.Disposed = true;
        }
    }
}
