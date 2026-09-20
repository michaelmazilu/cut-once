using UnityEngine;
using CutOnce.Copilot;

namespace CutOnce.Device
{
    /// <summary>
    /// Kit on the right controller's A button: hold to talk, release to send. Kept out of CutOnce.Copilot so that
    /// assembly needs no Meta reference; Unity's default assembly sees OVRInput as QuestCameraKit's scripts do.
    /// </summary>
    public class QuestPushToTalk : MonoBehaviour, IPushToTalk
    {
        public OVRInput.RawButton button = OVRInput.RawButton.A;

        bool _down, _up;
        int _frame = -1;

        /// <summary>Both edges are decided once per frame: CopilotController reads Down and then Up in the same one.</summary>
        void Refresh()
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            _down = OVRInput.GetDown(button, OVRInput.Controller.RTouch);
            _up = OVRInput.GetUp(button, OVRInput.Controller.RTouch);
        }

        public bool Down { get { Refresh(); return _down; } }
        public bool Up { get { Refresh(); return _up; } }
    }
}
