using System.Threading.Tasks;
using UnityEngine;

namespace CutOnce.AR
{
    /// <summary>What the operator's right controller is doing this frame. The Quest implementation reads OVRInput; nothing in CutOnce.AR knows about Meta.</summary>
    public interface IOperatorInput
    {
        bool TryGetPointer(out Ray ray);
        /// <summary>The point that "touch a corner" records. It is drawn as a small marker, so the operator sees exactly what is measured.</summary>
        Vector3 TipWorld { get; }
        Vector2 Stick { get; }
        bool TriggerDown { get; }
        bool TriggerHeld { get; }
        bool GripHeld { get; }
        bool MarkDown { get; }
        bool MarkHeld { get; }
        bool MarkUp { get; }
        bool StickClickHeld { get; }
    }

    /// <summary>Finds the real surface under the pointer (the Quest's depth sensing). May miss; the caller falls back to the floor.</summary>
    public interface ISurfaceRaycaster { bool Raycast(Ray ray, out Vector3 point); }

    /// <summary>
    /// Keeps the hologram fixed to the room across tracking corrections and app restarts. The returned transform is
    /// the anchor: AssemblyRoot is parented under it. Every call may fail (no room data yet, anchor from another
    /// room); null means "carry on without an anchor".
    /// </summary>
    public interface IAnchorStore
    {
        Task<Transform> Restore();
        Task<Transform> CreateAt(Pose worldPose);
        Task Forget();
        /// <summary>
        /// An anchor for this session only (a build-mode design): it pins the pose to the room like any other, but it is
        /// never saved, and the saved one is left exactly as it was, so the next launch still finds the last build.
        /// </summary>
        Task<Transform> CreateForSessionAt(Pose worldPose);
    }
}
