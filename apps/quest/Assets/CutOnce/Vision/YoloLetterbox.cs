using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace CutOnce.Vision
{
    /// <summary>YOLO input geometry: fit without stretching, then pad with RGB 114.</summary>
    public readonly struct YoloLetterboxLayout
    {
        public readonly Vector2Int sourceSize, modelSize, resizedSize, topLeftPadding;

        private YoloLetterboxLayout(Vector2Int sourceSize, Vector2Int modelSize,
            Vector2Int resizedSize, Vector2Int topLeftPadding)
        {
            this.sourceSize = sourceSize;
            this.modelSize = modelSize;
            this.resizedSize = resizedSize;
            this.topLeftPadding = topLeftPadding;
        }

        public static YoloLetterboxLayout Create(Vector2Int sourceSize, Vector2Int modelSize)
        {
            if (sourceSize.x <= 0 || sourceSize.y <= 0 || modelSize.x <= 0 || modelSize.y <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceSize), "Image and model dimensions must be positive.");
            var scale = Mathf.Min(modelSize.x / (float)sourceSize.x, modelSize.y / (float)sourceSize.y);
            var resized = new Vector2Int(
                Mathf.Clamp(Mathf.RoundToInt(sourceSize.x * scale), 1, modelSize.x),
                Mathf.Clamp(Mathf.RoundToInt(sourceSize.y * scale), 1, modelSize.y));
            // Equivalent to YOLO's round(halfPadding - .1) / round(halfPadding + .1).
            // An odd extra pixel goes on the bottom/right, not into a fractional image offset.
            var padding = new Vector2Int((modelSize.x - resized.x) / 2, (modelSize.y - resized.y) / 2);
            return new YoloLetterboxLayout(sourceSize, modelSize, resized, padding);
        }

        /// <summary>Bottom-left UV rectangle occupied by the actual image in the padded texture.</summary>
        public Vector4 ContentUvRect => new Vector4(
            topLeftPadding.x / (float)modelSize.x,
            (modelSize.y - resizedSize.y - topLeftPadding.y) / (float)modelSize.y,
            resizedSize.x / (float)modelSize.x, resizedSize.y / (float)modelSize.y);

        /// <summary>
        /// Model top-left pixel corners back into the original camera image's top-left pixels.
        /// Do not clamp: a partially visible object may legitimately extend beyond the image edge.
        /// Use the rounded raster dimensions so this exactly inverts what the GPU actually drew.
        /// </summary>
        public Rect ToSourceRect(Rect modelRect)
        {
            var scaleX = sourceSize.x / (float)resizedSize.x;
            var scaleY = sourceSize.y / (float)resizedSize.y;
            return new Rect((modelRect.x - topLeftPadding.x) * scaleX,
                (modelRect.y - topLeftPadding.y) * scaleY,
                modelRect.width * scaleX, modelRect.height * scaleY);
        }
    }

    /// <summary>
    /// Cached GPU-only letterboxing, shared by live camera inference and recorded-photo proof.
    /// The output is UNorm with gamma RGB values, so TextureConverter does not encode them twice.
    /// </summary>
    public sealed class YoloLetterbox : IDisposable
    {
        public YoloLetterboxLayout Layout { get; private set; }
        private RenderTexture _texture;
        private Material _material;
        private static readonly int ContentRectId = Shader.PropertyToID("_ContentRect");
        private static readonly int EncodeSrgbId = Shader.PropertyToID("_EncodeSrgb");

        public Texture Prepare(Texture source, Vector2Int modelSize)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Layout = YoloLetterboxLayout.Create(new Vector2Int(source.width, source.height), modelSize);
            if (_material == null)
            {
                var shader = Resources.Load<Shader>("YoloLetterbox");
                if (shader == null || !shader.isSupported)
                    throw new InvalidOperationException("The YoloLetterbox preprocessing shader is missing or unsupported.");
                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (_texture == null || !_texture.IsCreated() || _texture.width != modelSize.x || _texture.height != modelSize.y)
            {
                ReleaseTexture();
                _texture = new RenderTexture(modelSize.x, modelSize.y, 0, GraphicsFormat.R8G8B8A8_UNorm)
                {
                    name = "YOLO aspect-preserving RGB input", hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false, autoGenerateMips = false,
                };
                if (!_texture.Create()) throw new InvalidOperationException("Could not allocate the YOLO letterbox texture.");
            }
            _material.SetVector(ContentRectId, Layout.ContentUvRect);
            // Same rule as Inference Engine 2.6.1 TextureConverter: undo the hardware sRGB decode
            // when the source texture is sRGB. A UNorm source already contains numeric RGB values.
            _material.SetFloat(EncodeSrgbId, GraphicsFormatUtility.IsSRGBFormat(source.graphicsFormat) ? 1f : 0f);
            var previousSrgbWrite = GL.sRGBWrite;
            var previousActive = RenderTexture.active;
            try
            {
                GL.sRGBWrite = false;
                Graphics.Blit(source, _texture, _material);
            }
            finally
            {
                RenderTexture.active = previousActive;
                GL.sRGBWrite = previousSrgbWrite;
            }
            return _texture;
        }

        public void Dispose()
        {
            ReleaseTexture();
            if (_material != null) DestroyOwned(_material);
            _material = null;
        }

        private void ReleaseTexture()
        {
            if (_texture == null) return;
            _texture.Release();
            DestroyOwned(_texture);
            _texture = null;
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
