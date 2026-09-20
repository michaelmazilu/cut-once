using UnityEngine;

namespace CutOnce.Vision.PlayTests
{
    /// <summary>Matches PCA's relevant OnDisable texture-destruction behavior, not its native camera producer.</summary>
    public sealed class LifetimeTestTextureSource : MonoBehaviour
    {
        public RenderTexture Source;
        private void OnDisable()
        {
            if (Source == null) return;
            Destroy(Source);
            Source = null;
        }
    }
}
