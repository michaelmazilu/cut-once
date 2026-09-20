using System.Collections.Generic;
using NUnit.Framework;
using CutOnce.Copilot;
using CutOnce.Copilot.Net;
using UnityEngine;

public class CopilotClientTests
{
    class Host : ICopilotHost
    {
        public string AssemblyId => "asm_run_001";
        public int PlanRevision => 1;
        public int StateVersion => 3;
        public string Mode => "overlay";
        public string SelectedPartId => "part_moved_on_to";   // what the ray hits now, after the user moved
        public string SelectionSource => "controller_ray";
        public string CurrentStepId => "step_09";
        public IReadOnlyList<IProjectablePart> PartsForProjection() => new IProjectablePart[0];
        public void Highlight(string[] partIds, string style) { }
        public void HighlightTwins(string[] twinIds, string style) { }
        public void ShowCopilotActivity(CopilotActivity activity) { }
        public void ShowAnswer(CopilotResponseDto response) { }
        public void OnActionApplied(CopilotActionDto action) { }
        public void StepNav(string direction) { }
    }

    [Test] public void UsesTheSelectionFrozenAtPressNotTheHostsCurrentOne()
    {
        string json = CopilotClient.BuildContextJson(new Host(), new Selection("part_cable_tray", "controller_ray", "step_07"), new List<ProjectedPart>(), default, null);
        StringAssert.Contains("\"selected_part_id\":\"part_cable_tray\"", json);
        StringAssert.Contains("\"current_step_id\":\"step_07\"", json);
        StringAssert.DoesNotContain("part_moved_on_to", json);
    }

    [Test] public void NoFrameMeansCameraNullAndNoPartMeansSourceNone()
    {
        string json = CopilotClient.BuildContextJson(new Host(), new Selection(null, "controller_ray", null), new List<ProjectedPart>(), default, "q_cable");
        StringAssert.Contains("\"camera\":null", json);
        StringAssert.Contains("\"selected_part_id\":null", json);
        StringAssert.Contains("\"selection_source\":\"none\"", json);
        StringAssert.Contains("\"scripted_query_id\":\"q_cable\"", json);
    }

    [Test] public void WithAFrameTheCameraIsWritten()
    {
        var k = new CameraIntrinsics { width = 1280, height = 960, fx = 900, fy = 900, cx = 640, cy = 480 };
        StringAssert.Contains("\"camera\":{\"width\":1280,\"height\":960", CopilotClient.BuildContextJson(new Host(), Selection.Of(new Host()), new List<ProjectedPart>(), k, null));
    }

    [Test] public void ReadsRealObjectHighlightsFromTheServerResponse()
    {
        var response = JsonUtility.FromJson<CopilotResponseDto>("{\"turn_id\":\"turn_1\",\"highlight_parts\":[],\"highlight_twins\":[\"o1\",\"o12\"]}");
        CollectionAssert.AreEqual(new[] { "o1", "o12" }, response.highlight_twins);
    }
}
