using UnityEngine;
using CutOnce.Copilot;
using CutOnce.Copilot.Voice;

namespace CutOnce.Device
{
    /// <summary>
    /// Kit on the right controller's A button: hold to talk, release to send. Kept out of CutOnce.Copilot so that
    /// assembly needs no Meta reference; Unity's default assembly sees OVRInput as QuestCameraKit's scripts do.
    /// </summary>
    public class QuestPushToTalk : MonoBehaviour, IPushToTalk
    {
        public OVRInput.RawButton button = OVRInput.RawButton.A;

        bool _listening, _down, _up;
        int _frame = -1;
        float _startedAt;

        /// <summary>
        /// Both edges are decided once per frame: CopilotController reads Down and then Up in the same one.
        /// A is a TOGGLE — press to start, press again to send — so the two edges come from two presses, not from
        /// pressing and releasing. Holding a button down while you look at a table and point at things is the wrong
        /// shape for a headset, and one button for the whole copilot is the thing a demo needs.
        /// </summary>
        void Refresh()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _down = _up = false;
            // A clip stops itself at the recorder's limit. Forget it here too, so the next press starts a new one
            // instead of ending one that has already gone.
            if (_listening && Time.unscaledTime - _startedAt >= MicRecorder.MaxSeconds - 0.2f) _listening = false;
            if (!OVRInput.GetDown(button, OVRInput.Controller.RTouch)) return;
            if (_listening) { _up = true; _listening = false; }
            else { _down = true; _listening = true; _startedAt = Time.unscaledTime; }
        }

        public bool Down { get { Refresh(); return _down; } }
        public bool Up { get { Refresh(); return _up; } }
    }
}
