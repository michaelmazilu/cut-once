using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace CutOnce.Diagnostics
{
    /// <summary>
    /// Measures every frame against <see cref="QuestBudgets"/> and warns in the console when a budget is broken.
    /// Put one in every scene. In the simulator, the draw call and triangle numbers are the ones the headset will see
    /// (plans are loaded at runtime, so a scene scan cannot count them). The frame rate is only
    /// judged on the headset: in the Editor it is this computer's. Development builds on the headset also log the
    /// numbers every <see cref="logEverySeconds"/> seconds (adb logcat -s Unity, lines starting [Budget]).
    ///
    /// The public fields are the latest averages over <see cref="windowSeconds"/>, so an agent driving the simulator
    /// through Meta XR Operator can read them off this component.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class BudgetProbe : MonoBehaviour
    {
        [Tooltip("Seconds per measurement window.")]
        public float windowSeconds = 2f;
        [Tooltip("Development builds on the headset log the numbers this often. 0 turns it off.")]
        public float logEverySeconds = 10f;

        [Header("Latest averages (read-only)")]
        /// <summary>
        /// Unity's draw call or batch counter, whichever is larger. Inside the Editor under XR both read 0, so there a
        /// count of visible renderer-material pairs stands in: the same number the headset draws for the same content.
        /// </summary>
        public int drawCalls;
        public int triangles;
        /// <summary>
        /// Frames per second. On the headset a frame that misses its 13.9 ms slot waits for the next one, so the rate
        /// falls below 72: that is the budget that matters, and it is measured the same way on any runtime.
        /// </summary>
        public float fps;
        /// <summary>Frames in the window that took more than 1.5 frame slots (a visible judder on the headset).</summary>
        public int droppedFrames;
        public bool overBudget;

        const float SlowFrameFactor = 1.5f;
        const float FpsTolerance = 0.95f; // averaging noise, not a real miss

        ProfilerRecorder _drawCalls, _batches, _triangles;
        readonly List<Material> _materials = new();
        float _windowStart, _lastLog;
        int _frames, _slow;
        long _drawSum, _triSum;

        void OnEnable()
        {
            _drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            _batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            _triangles = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Triangles Count");
            _windowStart = _lastLog = Time.unscaledTime;
        }

        void OnDisable()
        {
            _drawCalls.Dispose();
            _batches.Dispose();
            _triangles.Dispose();
        }

        void LateUpdate()
        {
            _frames++;
            if (Time.unscaledDeltaTime * 1000f > QuestBudgets.FrameBudgetMs * SlowFrameFactor) _slow++;
            _drawSum += System.Math.Max(_drawCalls.Valid ? _drawCalls.LastValue : 0, _batches.Valid ? _batches.LastValue : 0);
            _triSum += _triangles.Valid ? _triangles.LastValue : 0;

            float elapsed = Time.unscaledTime - _windowStart;
            if (elapsed < windowSeconds) return;
            drawCalls = (int)(_drawSum / _frames);
            if (Application.isEditor) drawCalls = Mathf.Max(drawCalls, VisibleDrawCalls()); // the Editor's counters are silent under XR
            triangles = (int)(_triSum / _frames);
            fps = _frames / elapsed;
            droppedFrames = _slow;
            Report();
            _windowStart = Time.unscaledTime;
            _frames = 0; _slow = 0; _drawSum = 0; _triSum = 0;
        }

        /// <summary>One draw per material on each renderer some camera can see. SRP batching can merge a few.</summary>
        int VisibleDrawCalls()
        {
            int draws = 0;
            foreach (var r in FindObjectsByType<Renderer>(FindObjectsInactive.Exclude))
            {
                if (!r.enabled || !r.isVisible) continue;
                r.GetSharedMaterials(_materials);
                draws += _materials.Count;
            }
            return draws;
        }

        void Report()
        {
            var problems = new List<string>();
            if (drawCalls > QuestBudgets.MaxDrawCalls) problems.Add($"{drawCalls} draw calls (budget {QuestBudgets.MaxDrawCalls})");
            if (triangles > QuestBudgets.MaxTriangles) problems.Add($"{triangles:N0} triangles (budget {QuestBudgets.MaxTriangles:N0})");
            // The rate is only judged on the headset: a laptop is several times faster than the Quest 3.
            if (!Application.isEditor && fps < QuestBudgets.TargetFps * FpsTolerance)
                problems.Add($"{fps:F0} fps, {droppedFrames} dropped frame(s) (budget {QuestBudgets.TargetFps} fps)");
            bool was = overBudget;
            overBudget = problems.Count > 0;
            // Once on the way over and once on the way back, so the console stays readable.
            if (overBudget && !was) Debug.LogWarning("[Budget] over the Quest 3 budget: " + string.Join(", ", problems));
            else if (!overBudget && was) Debug.Log("[Budget] back inside the Quest 3 budget");

            if (Debug.isDebugBuild && !Application.isEditor && logEverySeconds > 0 && Time.unscaledTime - _lastLog >= logEverySeconds)
            {
                _lastLog = Time.unscaledTime;
                Debug.Log($"[Budget] {fps:F0} fps (budget {QuestBudgets.TargetFps}), {droppedFrames} dropped in {windowSeconds:F0} s, {drawCalls} draw calls, {triangles:N0} triangles");
            }
        }
    }
}
