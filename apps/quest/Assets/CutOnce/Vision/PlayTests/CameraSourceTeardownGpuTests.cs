using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.TestTools;

namespace CutOnce.Vision.PlayTests
{
    /// <summary>
    /// Opt-in native-resource teardown stress test. Unity does not document ordinary RenderTexture
    /// retention after Destroy during readback, and MRUK destroys its source in OnDisable. This
    /// test must not be reported as passed until executed; even an Editor pass is not Quest/PCA proof.
    /// </summary>
    public sealed class CameraSourceTeardownGpuTests
    {
        [UnityTest]
        [Explicit("Requires a graphics-enabled isolated Unity run; intentionally exercises undocumented source teardown timing. Not part of the normal check gate.")]
        public IEnumerator InvalidatedReadbackDrainsAfterSourceOwnerDisablesWithoutPublishingOrEarlyReuse()
        {
            if (!SystemInfo.supportsAsyncGPUReadback)
                Assert.Ignore("This graphics device cannot run the native readback teardown probe.");
            var owner = new GameObject("Borrowed PCA-like texture teardown probe");
            var source = owner.AddComponent<LifetimeTestTextureSource>();
            var capture = new AsyncCameraSnapshot();
            try
            {
                source.Source = new RenderTexture(64, 32, 0, GraphicsFormat.R8G8B8A8_UNorm);
                Assert.That(source.Source.Create(), Is.True);
                var previous = RenderTexture.active;
                try { RenderTexture.active = source.Source; GL.Clear(false, true, Color.green); }
                finally { RenderTexture.active = previous; }
                var stamp = new CameraCaptureStamp(new Pose(Vector3.zero, Quaternion.identity), DateTime.UtcNow,
                    new Vector2Int(64, 32), 1, Time.realtimeSinceStartupAsDouble);
                Assert.That(capture.TryBegin(source.Source, stamp), Is.True, capture.LastError);
                capture.Invalidate();
                Assert.That(capture.Status, Is.EqualTo(CameraSnapshotStatus.Draining));
                Assert.That(capture.TryBegin(source.Source, stamp), Is.False, "Outstanding request slot must stay occupied.");
                owner.SetActive(false); // Source OnDisable schedules Destroy, matching SDK 205.
                var deadline = Time.realtimeSinceStartupAsDouble + 8;
                while (capture.Poll(Time.realtimeSinceStartupAsDouble, 2, 8) == CameraSnapshotStatus.Draining &&
                    Time.realtimeSinceStartupAsDouble < deadline) yield return null;
                Assert.That(capture.Status, Is.EqualTo(CameraSnapshotStatus.Idle), "Native source teardown did not drain in 8 seconds.");
                Assert.That(capture.TryTake(out _), Is.False, "Invalidated source pixels must never publish.");
                Assert.That(capture.LastReadbackHadError, Is.False,
                    "This backend did not retain a valid source through teardown; do not claim source destruction is safe on it.");
            }
            finally
            {
                capture.Dispose();
                UnityEngine.Object.Destroy(owner);
            }
        }
    }
}
