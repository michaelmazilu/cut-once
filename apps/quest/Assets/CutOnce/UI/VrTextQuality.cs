using UnityEngine;
using UnityEngine.UI;

namespace CutOnce.UI
{
    /// <summary>
    /// Keeps legacy dynamic fonts sharp on world-space canvases. The canvas geometry stays the same physical size;
    /// only the font atlas is rasterised at a higher resolution before it is sampled by the Quest eye buffer.
    /// </summary>
    public static class VrTextQuality
    {
        // 8x is enough for labels near the viewer. The build HUD sits farther away and needs the denser atlas.
        public const float DynamicPixelsPerUnit = 8f;
        public const float DistantDynamicPixelsPerUnit = 12f;

        public static CanvasScaler AddScaler(GameObject canvasObject, float dynamicPixelsPerUnit = DynamicPixelsPerUnit)
        {
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.dynamicPixelsPerUnit = dynamicPixelsPerUnit;
            return scaler;
        }
    }
}
