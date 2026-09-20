namespace CutOnce.Vision
{
    /// <summary>
    /// One switch between the two ways the scanner can look.
    ///
    /// NORMAL (default, what a judge sees): passthrough, a subtle highlight on each recognised
    /// object, and a small label with its name. The blue fill, fine surface grid and rim follow
    /// measured real surfaces. Without live depth, only labels appear; no proxy boxes are drawn.
    ///
    /// DEBUG (opt-in): diagnostic detection bounds plus confidence, tracked id and size on every
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
