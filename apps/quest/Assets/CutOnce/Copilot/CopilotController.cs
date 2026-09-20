using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using CutOnce.Copilot.Net;
using CutOnce.Copilot.Voice;

namespace CutOnce.Copilot
{
    /// <summary>
    /// The whole push-to-talk turn, exactly as section 10 describes it:
    ///
    ///   A pressed   → freeze the selected part, grab one frame, start the mic, show the listening ring
    ///   while held  → nothing else, max 12 s
    ///   A released  → stop the mic, project every part into the frozen frame, send one request
    ///   response    → highlight at once, show the text and the source card, stream the audio
    ///
    /// Two rules worth keeping in your head while editing this:
    ///   1. The frame and the selection are frozen on PRESS, not on release. The user moves while talking.
    ///      A HUD button captures its own; a frame is never reused by a later question.
    ///   2. A `mark_state` action has ALREADY been written by the server. Show an Undo toast; do not
    ///      append an event, or the same command lands twice.
    ///
    /// Put this on the [Copilot] prefab. Nothing else in the app should reference the copilot's internals.
    /// </summary>
    public class CopilotController : MonoBehaviour
    {
        [Header("Server")]
        public string baseUrl = "http://127.0.0.1:8080";
        public string apiToken = "dev-token";

        [Header("Wiring")]
        public MonoBehaviour frameSourceBehaviour;   // any ICameraFrameSource: Device/PcaFrameSource on the headset, FixtureFrameSource in the Editor
        public MonoBehaviour hostBehaviour;          // A2's ICopilotHost implementation
        public MonoBehaviour pushToTalkBehaviour;    // any IPushToTalk: Device/QuestPushToTalk (the A button) on the headset
        public MicRecorder mic;
        public PcmStreamPlayer speaker;

        public bool IsListening { get; private set; }
        public bool IsThinking { get; private set; }
        /// <summary>The last turn's time to first audio. Watch this during rehearsal: the target is under 5 s.</summary>
        public float LastFirstAudioMs { get; private set; } = -1f;

        private ICameraFrameSource _frames;
        private ICopilotHost _host;
        private IPushToTalk _ptt;
        private CopilotClient _client;
        private CameraFrame _pressFrame;         // frozen when the button goes down, cleared once sent
        private Selection _pressSelection;

        private void Awake()
        {
            _frames = frameSourceBehaviour as ICameraFrameSource;
            _host = hostBehaviour as ICopilotHost;
            _ptt = pushToTalkBehaviour as IPushToTalk;
            _client = new CopilotClient(baseUrl, apiToken);
            if (_frames == null) Debug.LogError("[Copilot] frameSourceBehaviour does not implement ICameraFrameSource.");
            if (_host == null) Debug.LogError("[Copilot] hostBehaviour does not implement ICopilotHost.");
            if (_ptt == null) Debug.LogError("[Copilot] pushToTalkBehaviour does not implement IPushToTalk.");
        }

        private void Update()
        {
            if (_host == null || _ptt == null) return;
            if (_ptt.Down) BeginListening();
            else if (IsListening && (_ptt.Up || mic.ElapsedSeconds >= MicRecorder.MaxSeconds - 0.2f)) EndListening();
        }

        /// <summary>Also the entry point for a HUD query button: pass the rehearsed question's id.</summary>
        public void AskScripted(string scriptedQueryId)
        {
            if (IsListening || IsThinking || _host == null) return;
            // A HUD button is its own press: take the frame and the selection now, never a previous question's.
            StartCoroutine(Send(null, scriptedQueryId, CaptureNow(), Selection.Of(_host)));
        }

        private CameraFrame CaptureNow() => _frames != null && _frames.IsReady ? _frames.Capture() : default;

        private void BeginListening()
        {
            if (IsListening || IsThinking) return;
            // Frozen on press: the answer must be about what they were looking at when they asked.
            _pressFrame = CaptureNow();
            _pressSelection = Selection.Of(_host);
            if (!mic.Begin()) { _pressFrame = default; return; }
            IsListening = true;
        }

        private void EndListening()
        {
            IsListening = false;
            byte[] wav = mic.End();
            CameraFrame frame = _pressFrame;
            _pressFrame = default; // never reused by a later question
            if (wav == null || wav.Length < 1000)
            {
                Debug.Log("[Copilot] nothing recorded; ignoring.");
                return;
            }
            StartCoroutine(Send(wav, null, frame, _pressSelection));
        }

        private IEnumerator Send(byte[] wav, string scriptedQueryId, CameraFrame frame, Selection selection)
        {
            IsThinking = true;
            LastFirstAudioMs = -1f;

            // No pixels is survivable: the server answers from the tables and documents (camera null, no frame part).
            if (!frame.IsValid) Debug.LogWarning("[Copilot] no camera frame; asking without one.");
            var visible = frame.IsValid ? PartProjector.Project(_host.PartsForProjection(), frame) : new List<ProjectedPart>();
            string context = CopilotClient.BuildContextJson(_host, selection, visible, frame.Intrinsics, scriptedQueryId);

            yield return _client.Query(
                _host.AssemblyId, context, wav ?? MicRecorder.EncodeWav(new float[160], MicRecorder.SampleRate, 1),
                frame.IsValid ? frame.Jpeg : null,
                OnAnswer,
                OnFailed);
        }

        /// <summary>Never a silent failure: the HUD says nothing is coming and what to do.</summary>
        private void OnFailed(string error)
        {
            IsThinking = false;
            Debug.LogWarning("[Copilot] " + error);
            _host.ShowAnswer(new CopilotResponseDto
            {
                answer_text = "I couldn't answer that. Press A and ask again.",
                needs_clarification = true, highlight_parts = new string[0], drawing_refs = new DrawingRefDto[0],
            });
        }

        private void OnAnswer(CopilotResponseDto response)
        {
            IsThinking = false;

            // Highlight first: it lands in the same frame the text appears, before any audio.
            if (response.highlight_parts != null && response.highlight_parts.Length > 0)
                _host.Highlight(response.highlight_parts, response.highlight_style);
            _host.ShowAnswer(response);

            if (response.HasAction)
            {
                if (response.action.type == "step_nav") _host.StepNav(response.action.direction);
                else _host.OnActionApplied(response.action);   // already written by the server: offer Undo, do not re-append
            }

            if (!string.IsNullOrEmpty(response.audio_url) && speaker != null)
            {
                speaker.Play(baseUrl, response.audio_url, apiToken);
                StartCoroutine(TrackFirstAudio());
            }
        }

        private IEnumerator TrackFirstAudio()
        {
            float started = Time.realtimeSinceStartup;
            while (speaker.FirstAudioMs < 0f && Time.realtimeSinceStartup - started < 10f) yield return null;
            LastFirstAudioMs = speaker.FirstAudioMs;
            if (LastFirstAudioMs > 5000f) Debug.LogWarning($"[Copilot] first audio took {LastFirstAudioMs:0} ms — over the G6 target.");
        }
    }
}
