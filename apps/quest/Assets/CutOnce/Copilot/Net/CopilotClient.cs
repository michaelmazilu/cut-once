using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace CutOnce.Copilot.Net
{
    /// <summary>
    /// Talks to the copilot endpoints. The context packet is written by hand rather than through
    /// JsonUtility because it needs real nulls and nested number arrays, neither of which JsonUtility
    /// can produce — and this packet is a frozen contract with the server.
    /// </summary>
    public class CopilotClient
    {
        private readonly string _baseUrl;
        private readonly string _token;

        public CopilotClient(string baseUrl, string token)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _token = token;
        }

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        private static string Quote(string s) => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        private static string NewId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N").Substring(0, 20).ToLowerInvariant();

        /// <summary>The CopilotContext. `selection` is what was pointed at when the button went down, not now.</summary>
        public static string BuildContextJson(ICopilotHost host, Selection selection, List<ProjectedPart> visible, CameraIntrinsics k, string scriptedQueryId)
        {
            var sb = new StringBuilder(1024);
            sb.Append("{");
            sb.Append("\"context_id\":").Append(Quote(NewId("ctx"))).Append(",");
            sb.Append("\"assembly_id\":").Append(Quote(host.AssemblyId)).Append(",");
            sb.Append("\"plan_revision\":").Append(host.PlanRevision).Append(",");
            sb.Append("\"state_version\":").Append(host.StateVersion).Append(",");
            sb.Append("\"mode\":").Append(Quote(host.Mode)).Append(",");
            sb.Append("\"selected_part_id\":").Append(Quote(selection.PartId)).Append(",");
            sb.Append("\"selection_source\":").Append(Quote(selection.Source)).Append(",");
            sb.Append("\"current_step_id\":").Append(Quote(selection.StepId)).Append(",");

            sb.Append("\"visible_parts\":[");
            for (int i = 0; i < visible.Count; i++)
            {
                var p = visible[i];
                if (i > 0) sb.Append(",");
                sb.Append("{\"part_id\":").Append(Quote(p.PartId))
                  .Append(",\"state\":").Append(Quote(p.State))
                  .Append(",\"bbox_px\":[").Append(F(p.X)).Append(",").Append(F(p.Y)).Append(",").Append(F(p.W)).Append(",").Append(F(p.H)).Append("]")
                  .Append(",\"in_frame\":").Append(F(p.InFrame))
                  .Append(",\"distance_m\":").Append(F(p.DistanceM)).Append("}");
            }
            sb.Append("],");

            // No frame, no camera: the schema wants null, and a zero-sized camera is rejected with a 400.
            if (k.width > 0 && k.height > 0)
                sb.Append("\"camera\":{\"width\":").Append(k.width).Append(",\"height\":").Append(k.height)
                  .Append(",\"fx\":").Append(F(k.fx)).Append(",\"fy\":").Append(F(k.fy))
                  .Append(",\"cx\":").Append(F(k.cx)).Append(",\"cy\":").Append(F(k.cy)).Append("},");
            else
                sb.Append("\"camera\":null,");

            sb.Append("\"scripted_query_id\":").Append(Quote(scriptedQueryId)).Append(",");
            sb.Append("\"client_sent_at\":").Append(Quote(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)));
            sb.Append("}");
            return sb.ToString();
        }

        /// <summary>One question. `onDone` gets the parsed response, or null with a reason in `onError`.</summary>
        public IEnumerator Query(string assemblyId, string contextJson, byte[] wav, byte[] jpeg,
                                 Action<CopilotResponseDto> onDone, Action<string> onError, float timeoutSeconds = 25f)
        {
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("context", contextJson),
                new MultipartFormFileSection("audio", wav, "turn.wav", "audio/wav"),
            };
            // No frame is allowed (the server answers from the tables and documents); an empty file section is not.
            if (jpeg != null && jpeg.Length > 0) form.Add(new MultipartFormFileSection("frame", jpeg, "frame.jpg", "image/jpeg"));

            using var request = UnityWebRequest.Post($"{_baseUrl}/v1/assemblies/{assemblyId}/copilot/query", form);
            request.SetRequestHeader("Authorization", "Bearer " + _token);
            request.timeout = Mathf.CeilToInt(timeoutSeconds);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"{request.responseCode} {request.error}: {request.downloadHandler?.text}");
                yield break;
            }
            CopilotResponseDto parsed = null;
            try { parsed = JsonUtility.FromJson<CopilotResponseDto>(request.downloadHandler.text); }
            catch (Exception e) { onError?.Invoke("could not read the answer: " + e.Message); yield break; }

            if (parsed == null || string.IsNullOrEmpty(parsed.turn_id)) { onError?.Invoke("the answer had no turn_id"); yield break; }
            onDone?.Invoke(parsed);
        }

        /// <summary>Section 11's camera check. `expectedView` may be null; the server then prefers `unsure`.</summary>
        public IEnumerator Verify(string assemblyId, string requestJson, byte[] frameJpeg, byte[] expectedViewJpeg,
                                  Action<VerificationResultDto> onDone, Action<string> onError, float timeoutSeconds = 10f)
        {
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("request", requestJson),
                new MultipartFormFileSection("frame", frameJpeg, "frame.jpg", "image/jpeg"),
            };
            if (expectedViewJpeg != null) form.Add(new MultipartFormFileSection("expected_view", expectedViewJpeg, "expected.jpg", "image/jpeg"));

            using var request = UnityWebRequest.Post($"{_baseUrl}/v1/assemblies/{assemblyId}/verify", form);
            request.SetRequestHeader("Authorization", "Bearer " + _token);
            request.timeout = Mathf.CeilToInt(timeoutSeconds);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success) { onError?.Invoke(request.error); yield break; }
            try { onDone?.Invoke(JsonUtility.FromJson<VerificationResultDto>(request.downloadHandler.text)); }
            catch (Exception e) { onError?.Invoke(e.Message); }
        }

        /// <summary>G2's loop: post whatever was captured so it shows up on /debug. Not on the demo path.</summary>
        public IEnumerator DebugCapture(byte[] jpeg, byte[] wav, string note, string contextJson)
        {
            var form = new List<IMultipartFormSection> { new MultipartFormDataSection("note", note ?? "from the quest") };
            if (jpeg != null) form.Add(new MultipartFormFileSection("frame", jpeg, "frame.jpg", "image/jpeg"));
            if (wav != null) form.Add(new MultipartFormFileSection("audio", wav, "clip.wav", "audio/wav"));
            if (!string.IsNullOrEmpty(contextJson)) form.Add(new MultipartFormDataSection("context", contextJson));

            using var request = UnityWebRequest.Post($"{_baseUrl}/v1/copilot/debug/capture", form);
            request.SetRequestHeader("Authorization", "Bearer " + _token);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) Debug.LogWarning("[Copilot] debug capture failed: " + request.error);
        }

        public static string BuildVerificationJson(string partId, string claimedState, int stateVersion, ProjectedPart box, CameraIntrinsics k)
        {
            return "{\"verification_id\":" + Quote(NewId("ver")) +
                   ",\"part_id\":" + Quote(partId) +
                   ",\"claimed_state\":" + Quote(claimedState) +
                   ",\"state_version\":" + stateVersion +
                   ",\"bbox_px\":[" + F(box.X) + "," + F(box.Y) + "," + F(box.W) + "," + F(box.H) + "]" +
                   ",\"in_frame\":" + F(box.InFrame) +
                   ",\"camera\":{\"width\":" + k.width + ",\"height\":" + k.height +
                   ",\"fx\":" + F(k.fx) + ",\"fy\":" + F(k.fy) + ",\"cx\":" + F(k.cx) + ",\"cy\":" + F(k.cy) + "}}";
        }
    }
}
