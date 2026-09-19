using UnityEngine;

namespace CutOnce.AR
{
    /// <summary>
    /// Reveals a part bottom-up by world height. Renderer.bounds is in world space whatever the pivot, so desk boxes
    /// (centre pivots) and model parts (pivots at the origin) rise the same way. Never scale a part to animate it.
    /// </summary>
    public static class Reveal
    {
        public static readonly int RevealY = Shader.PropertyToID("_RevealY");
        /// <summary>Shader default for "fully shown" (the property's default value in CutOnce/Hologram).</summary>
        public const float FullyShown = 1e6f;

        /// <summary>World height of the cut for progress t in [0, 1]; everything above it is clipped.</summary>
        public static float CutHeight(Bounds worldBounds, float t) => Mathf.Lerp(worldBounds.min.y - 0.001f, worldBounds.max.y, Mathf.Clamp01(t));
    }
}
