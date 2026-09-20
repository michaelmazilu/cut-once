using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace CutOnce.Vision
{
    /// <summary>The texture is borrowed from its snapshot owner and immutable until Release is called.</summary>
    public readonly struct CameraSnapshot
    {
        public Texture2D Texture { get; }
        public CameraCaptureStamp Stamp { get; }
        public CameraSnapshot(Texture2D texture, CameraCaptureStamp stamp) { Texture = texture; Stamp = stamp; }
    }

    /// <summary>
    /// Latest-image route documented by MRUK 205: asynchronously read the ORIGINAL PCA texture,
    /// never a Graphics.Blit of it. Upload raw RGBA bytes to a reusable owned texture with the SAME
    /// sRGB/UNorm format, so later inference cannot accidentally sample a newer camera frame.
    /// The SDK's sensor/render pairing and Quest performance still require hardware validation.
    /// The caller owns the source texture and must keep it alive until Pending/Draining completes,
    /// even if this helper is disposed. Every successful TryTake requires Release in a finally.
    /// </summary>
    public sealed class AsyncCameraSnapshot : IDisposable
    {
        private readonly CameraCaptureState _state = new CameraCaptureState();
        private AsyncGPUReadbackRequest _request;
        private GraphicsFormat _sourceFormat;
        private Texture2D _texture;
        private bool _leased;
        public CameraSnapshotStatus Status => _state.Status;
        public CameraSnapshotRejection LastRejection => _state.LastRejection;
        public string LastError { get; private set; } = "";
        public bool LastReadbackHadError { get; private set; }

        public bool TryBegin(Texture source, CameraCaptureStamp stamp)
        {
            if (Status != CameraSnapshotStatus.Idle) return false;
            if (source == null || source.width != stamp.Resolution.x || source.height != stamp.Resolution.y)
            { LastError = "Camera snapshot source dimensions do not match its metadata."; return false; }
            var format = source.graphicsFormat;
            if (format != GraphicsFormat.R8G8B8A8_SRGB && format != GraphicsFormat.R8G8B8A8_UNorm)
            { LastError = "Camera snapshot requires original RGBA8 sRGB or UNorm pixels; no implicit format conversion is permitted."; return false; }
            if (!SystemInfo.supportsAsyncGPUReadback || !SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.ReadPixels))
            { LastError = "Asynchronous RGBA camera readback is unsupported on this graphics device."; return false; }
            if (!_state.TryBegin(stamp)) { LastError = "Camera snapshot metadata is invalid."; return false; }
            _sourceFormat = format;
            LastError = "";
            LastReadbackHadError = false;
            try
            {
                // No destination-format argument: retain the source's actual encoded bytes.
                // Unity owns request memory and reclaims it after completion; we allocate no
                // per-pixel managed array or persistent readback buffer that could be freed early.
                _request = AsyncGPUReadback.Request(source, 0);
                return true;
            }
            catch (Exception error)
            {
                _state.Complete(false, stamp.AcquiredAtRealtimeSeconds, stamp.Generation, .5);
                LastError = "Camera snapshot request failed: " + error.Message;
                return false;
            }
        }

        public CameraSnapshotStatus Poll(double now, int currentGeneration, double maximumAgeSeconds = .5)
        {
            if (Status == CameraSnapshotStatus.Disposed) return Status;
            _state.Check(now, currentGeneration, maximumAgeSeconds);
            UpdatePolicyError();
            if (Status != CameraSnapshotStatus.Pending && Status != CameraSnapshotStatus.Draining) return Status;
            if (!_request.done)
            {
                if (now - _state.Stamp.AcquiredAtRealtimeSeconds > 8)
                    LastError = "Camera snapshot readback stalled beyond 8 seconds; invalid frame is being drained before reuse.";
                return Status;
            }
            LastReadbackHadError = _request.hasError;
            if (!_state.Complete(!LastReadbackHadError, now, currentGeneration, maximumAgeSeconds))
            {
                UpdatePolicyError();
                return Status;
            }
            try
            {
                var stamp = _state.Stamp;
                var pixels = _request.GetData<byte>();
                var expectedBytes = (long)stamp.Resolution.x * stamp.Resolution.y * 4;
                if (_request.width != stamp.Resolution.x || _request.height != stamp.Resolution.y || pixels.Length != expectedBytes)
                    throw new InvalidOperationException("Readback dimensions/byte count do not match the captured image.");
                if (_texture == null || _texture.width != stamp.Resolution.x || _texture.height != stamp.Resolution.y || _texture.graphicsFormat != _sourceFormat)
                {
                    DestroyTexture();
                    _texture = new Texture2D(stamp.Resolution.x, stamp.Resolution.y, TextureFormat.RGBA32, false,
                        linear: _sourceFormat == GraphicsFormat.R8G8B8A8_UNorm)
                    {
                        name = "Immutable captured camera RGBA", hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                    };
                    if (_texture.graphicsFormat != _sourceFormat) throw new InvalidOperationException("Snapshot upload format differs from original PCA format.");
                }
                // Consume the request's temporary NativeArray in its completion frame. Neither
                // GetData nor LoadRawTextureData creates a managed per-pixel array.
                _texture.LoadRawTextureData(pixels);
                _texture.Apply(false, false);
                LastError = "";
            }
            catch (Exception error)
            {
                _state.Invalidate(CameraSnapshotRejection.ReadbackFailed);
                LastError = "Camera snapshot upload failed: " + error.Message;
            }
            return Status;
        }

        public bool TryTake(out CameraSnapshot snapshot)
        {
            snapshot = default;
            if (!_state.TryTake()) return false;
            _leased = true;
            snapshot = new CameraSnapshot(_texture, _state.Stamp);
            return true;
        }

        public void Release()
        {
            _leased = false;
            _state.Release();
            if (Status == CameraSnapshotStatus.Disposed) DestroyTexture();
        }

        public void Invalidate() => _state.Invalidate();

        public void Dispose()
        {
            _state.Dispose();
            // An in-flight readback targets the BORROWED PCA texture and Unity-owned request
            // memory, not _texture. There is no callback/owned readback buffer to cancel or free.
            // Never destroy the PCA source; release a leased snapshot only when its consumer ends.
            if (!_leased) DestroyTexture();
        }

        private void UpdatePolicyError()
        {
            switch (_state.LastRejection)
            {
                case CameraSnapshotRejection.Stale: LastError = "Camera snapshot expired before use; acquisition age includes readback time."; break;
                case CameraSnapshotRejection.GenerationChanged: LastError = "Camera snapshot discarded after pause, disable, or stream restart."; break;
                case CameraSnapshotRejection.MetadataChanged: LastError = "Camera metadata changed while queuing its snapshot."; break;
                case CameraSnapshotRejection.InvalidMetadata: LastError = "Camera snapshot has invalid timing or metadata."; break;
                case CameraSnapshotRejection.ReadbackFailed: LastError = "Asynchronous camera snapshot readback failed."; break;
            }
        }

        private void DestroyTexture()
        {
            if (_texture == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(_texture);
            else UnityEngine.Object.DestroyImmediate(_texture);
            _texture = null;
        }
    }
}
