using UnityEngine;
using CutOnce.Copilot;
using CutOnce.Copilot.Voice;

namespace CutOnce.Device
{
    /// <summary>
    /// Kit on the right controller's A button: press once to start listening, press again to send. One button for the
    /// whole copilot — questions, "what can I build?", picking a design, "done" — because a demo is no place to
    /// remember which button does what, and holding a button while you talk and point is worse.
    ///
    /// CopilotController listens for a press and a release, so a toggle is reported as those two edges: the first
    /// press is the "press", the second is the "release". Kept out of CutOnce.Copilot so that assembly needs no Meta
    /// reference; Unity's default assembly sees OVRInput as QuestCameraKit's scripts do.
    /// </summary>
    public class QuestPushToTalk : MonoBehaviour, IPushToTalk
    {
        public OVRInput.RawButton button = OVRInput.RawButton.A;

        bool _listening, _down, _up;
        int _frame = -1;
        float _startedAt;

        /// <summary>Both edges are decided once per frame: CopilotController reads Down and then Up in the same one.</summary>
        void Refresh()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _down = _up = false;
            // A clip stops itself at the recorder's limit. Forget it here too, so the next press starts a new one
            // instead of ending one that has already been sent.
            if (_listening && Time.unscaledTime - _startedAt >= MicRecorder.MaxSeconds - 0.2f) _listening = false;
            if (!OVRInput.GetDown(button)) return;
            if (_listening) { _up = true; _listening = false; }
            else { _down = true; _listening = true; _startedAt = Time.unscaledTime; }
        }

        public bool Down { get { Refresh(); return _down; } }
        public bool Up { get { Refresh(); return _up; } }
    }
}
