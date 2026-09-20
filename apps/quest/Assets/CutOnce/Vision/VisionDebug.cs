namespace CutOnce.Vision
{
    /// <summary>
    /// One switch between the two ways the scanner can look.
    ///
    /// NORMAL (default, what a judge sees): passthrough, a subtle highlight on each recognised
    /// object, and a small label with its name. Nothing else — no room mesh, no grids, no boxes,
    /// no confidence numbers. The room looks like the room.
    ///
    /// DEBUG (opt-in): the same view plus the numbers — confidence, tracked id, size — on every
    /// label. Toggled on device by clicking BOTH thumbsticks in and holding for a second, or set
    /// from code or the Inspector before a session.
    ///
    /// Everything invisible (depth raycasts, scene mesh colliders, MRUK anchors, tracking) runs
    /// identically in both modes: this flag only ever changes what is DRAWN, never what is known.
    /// </summary>
    public static class VisionDebug
    {
        public static bool Enabled;
    }
}
