using System.Collections;
using CutOnce.AR;
using CutOnce.Core;
using CutOnce.Placement;
using CutOnce.UI;
using Meta.XR;
using UnityEngine;

namespace CutOnce.Device
{
    /// <summary>
    /// Main-app bridge: aim at a full-size box target to check its visible physical surfaces.
    /// Advisory only: Matches must never be translated to a part_state event or physical-instance identity.
    /// The user still marks built with B, and losing evidence removes the live green feedback.
    /// </summary>
    public sealed class LivePlacementCheck : MonoBehaviour
    {
        readonly SurfacePlacementCheck checker = new SurfacePlacementCheck();
        AssemblyView assembly;
        AlignmentController alignment;
        SelectionController selection;
        BuildMode build;
        HudController hud;
        QuestPlacementSurface source;
        PartView current;
        Mesh mesh;
        VisualStyle original, feedback;
        string message;
        double nextSample;

        public void Init(AssemblyView view, AlignmentController align, SelectionController pointer, BuildMode mode, HudController panel)
        { assembly = view; alignment = align; selection = pointer; build = mode; hud = panel; }

        IEnumerator Start()
        {
            source = gameObject.AddComponent<QuestPlacementSurface>();
            var delay = new WaitForSecondsRealtime(1);
            while (true)
            {
                var camera = FindAnyObjectByType<PassthroughCameraAccess>();
                var depth = FindAnyObjectByType<EnvironmentRaycastManager>();
                if (camera != null && depth != null) { source.Init(depth, camera); yield break; }
                yield return delay;
            }
        }

        void LateUpdate()
        {
            if (assembly == null || alignment == null || selection == null || hud == null) return;
            bool allowed = alignment.State == AlignmentState.Locked && assembly.gameObject.activeInHierarchy &&
                hud.gameObject.activeInHierarchy && assembly.DisplayScale == 1f && (build == null || !build.PiecesInFlight);
            var target = allowed ? assembly.ViewOf(selection.SelectedPartId) : null;
            if (target == null)
            {
                // Unity's destroyed-object == null must not leave the last run's success text on screen.
                if (!ReferenceEquals(current, null) || !string.IsNullOrEmpty(message)) Clear();
                return;
            }
            if (target != current)
            {
                Clear(); current = target;
                if (current != null)
                {
                    mesh = current.GetComponent<MeshFilter>()?.sharedMesh;
                    original = current.Style;
                    feedback = original?.Clone();
                }
            }
            if (current == null) return;
            // A normal state/selection refresh owns its own style. Preserve it for when checking ends.
            if (current.Style != feedback && current.Style != original) { original = current.Style; feedback = original?.Clone(); }
            if (current.Part.shape?.type != "box" || mesh == null)
            { checker.Reset(); Say("Placement check: box shapes only. B still marks built."); Restore(); return; }
            var size = mesh.bounds.size;
            if (Mathf.Min(size.x, Mathf.Min(size.y, size.z)) < .08f)
            { checker.Reset(); Say("Too thin for a reliable depth check. Inspect it before marking built."); Restore(); return; }
            double now = Time.unscaledTimeAsDouble;
            if (now >= nextSample)
            {
                nextSample = now + .1; // depth query budget: at most 27 rays, 10 checks/second, 3 ms per check
                if (source == null || !source.TryFrame(out var eye, out var timestamp))
                { checker.Reset(); Say("Placement check needs live camera + depth on Quest."); }
                else
                {
                    var state = checker.Evaluate(source, eye, current.transform.localToWorldMatrix, mesh.bounds, timestamp, now);
                    Say(state == SurfacePlacementState.Matches ? "Visible surfaces match. Check the object, then B to mark built." :
                        state == SurfacePlacementState.Aligning ? "Surfaces lining up — hold still…" :
                        state == SurfacePlacementState.Mismatch ? "Surfaces don't match yet. Adjust the object or clear the view." :
                        "Show two faces of the object clearly; keep hands out of the way.");
                }
            }
            if (feedback == null) return;
            var result = checker.State;
            if (result == SurfacePlacementState.Matches || result == SurfacePlacementState.Aligning)
            {
                feedback.fill = feedback.edge = result == SurfacePlacementState.Matches ? "#33E68C" : "#FFC14D";
                feedback.fillAlpha = .12; feedback.edgeAlpha = 1; feedback.grid = false; feedback.pulseHz = 0;
                current.Apply(feedback);
            }
            else Restore();
        }

        void Say(string text) { if (message == text) return; message = text; hud.ShowPlacement(text); }
        void Restore() { if (current != null && original != null && current.Style == feedback) current.Apply(original); }
        void Clear()
        {
            Restore(); checker.Reset(); current = null; mesh = null; original = feedback = null; nextSample = 0;
            message = null; if (hud != null) hud.ShowPlacement("");
        }
        void OnDisable() => Clear();
    }
}
