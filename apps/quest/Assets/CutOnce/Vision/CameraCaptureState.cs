using System;
using UnityEngine;

namespace CutOnce.Vision
{
    /// <summary>Metadata frozen when the original PCA texture readback is enqueued, not when it completes.</summary>
    public readonly struct CameraCaptureStamp
    {
        public Pose Pose { get; }
        public DateTime CameraTimestamp { get; }
        public Vector2Int Resolution { get; }
        public int Generation { get; }
        /// <summary>Unity monotonic application time; a lower bound on sensor age, not a converted UTC timestamp.</summary>
        public double AcquiredAtRealtimeSeconds { get; }
        public CapturedCameraProjection Projection { get; }

        public CameraCaptureStamp(Pose pose, DateTime cameraTimestamp, Vector2Int resolution, int generation,
            double acquiredAtRealtimeSeconds, CapturedCameraProjection projection = default)
        {
            Pose = pose;
            CameraTimestamp = cameraTimestamp;
            Resolution = resolution;
            Generation = generation;
            AcquiredAtRealtimeSeconds = acquiredAtRealtimeSeconds;
            Projection = projection;
        }
    }

    public enum CameraSnapshotStatus { Idle, Pending, Ready, Leased, Draining, Disposed }
    public enum CameraSnapshotRejection
    {
        None, Busy, InvalidMetadata, Stale, GenerationChanged, MetadataChanged, ReadbackFailed, Disposed,
    }

    /// <summary>
    /// Pure single-flight ownership and freshness policy. Invalidating a pending request never
    /// makes its slot reusable until that request completes. A leased texture remains owned by
    /// its consumer until Release, even if the capture becomes stale or its stream restarts.
    /// </summary>
    public sealed class CameraCaptureState
    {
        public CameraSnapshotStatus Status { get; private set; }
        public CameraSnapshotRejection LastRejection { get; private set; }
        public CameraCaptureStamp Stamp { get; private set; }

        public bool TryBegin(CameraCaptureStamp stamp)
        {
            if (Status == CameraSnapshotStatus.Disposed) { LastRejection = CameraSnapshotRejection.Disposed; return false; }
            // A refused caller must not overwrite the original pending/draining rejection.
            if (Status != CameraSnapshotStatus.Idle) return false;
            if (!Valid(stamp)) { LastRejection = CameraSnapshotRejection.InvalidMetadata; return false; }
            Stamp = stamp;
            LastRejection = CameraSnapshotRejection.None;
            Status = CameraSnapshotStatus.Pending;
            return true;
        }

        public void Check(double now, int currentGeneration, double maximumAgeSeconds)
        {
            if (Status == CameraSnapshotStatus.Idle || Status == CameraSnapshotStatus.Disposed) return;
            if (Stamp.Generation != currentGeneration) { Invalidate(CameraSnapshotRejection.GenerationChanged); return; }
            if (!Finite(now) || now < Stamp.AcquiredAtRealtimeSeconds || !Finite(maximumAgeSeconds) || maximumAgeSeconds <= 0)
            { Invalidate(CameraSnapshotRejection.InvalidMetadata); return; }
            if (now - Stamp.AcquiredAtRealtimeSeconds > maximumAgeSeconds) Invalidate(CameraSnapshotRejection.Stale);
        }

        public bool Complete(bool succeeded, double now, int currentGeneration, double maximumAgeSeconds)
        {
            if (Status != CameraSnapshotStatus.Pending && Status != CameraSnapshotStatus.Draining) return false;
            Check(now, currentGeneration, maximumAgeSeconds);
            if (Status == CameraSnapshotStatus.Draining) { Status = CameraSnapshotStatus.Idle; return false; }
            if (!succeeded)
            {
                LastRejection = CameraSnapshotRejection.ReadbackFailed;
                Status = CameraSnapshotStatus.Idle;
                return false;
            }
            Status = CameraSnapshotStatus.Ready;
            LastRejection = CameraSnapshotRejection.None;
            return true;
        }

        public bool TryTake()
        {
            if (Status != CameraSnapshotStatus.Ready) return false;
            Status = CameraSnapshotStatus.Leased;
            return true;
        }

        public void Release()
        {
            if (Status == CameraSnapshotStatus.Leased) Status = CameraSnapshotStatus.Idle;
        }

        public void Invalidate(CameraSnapshotRejection reason = CameraSnapshotRejection.GenerationChanged)
        {
            if (Status == CameraSnapshotStatus.Disposed || Status == CameraSnapshotStatus.Idle) return;
            // Keep the original reason during drainage (e.g. pause followed by a long stall).
            if (Status != CameraSnapshotStatus.Draining) LastRejection = reason;
            if (Status == CameraSnapshotStatus.Pending) Status = CameraSnapshotStatus.Draining;
            else if (Status == CameraSnapshotStatus.Ready) Status = CameraSnapshotStatus.Idle;
        }

        public void Dispose()
        {
            LastRejection = CameraSnapshotRejection.Disposed;
            Status = CameraSnapshotStatus.Disposed;
        }

        private static bool Valid(CameraCaptureStamp stamp)
        {
            if (stamp.CameraTimestamp == default || stamp.Resolution.x <= 0 || stamp.Resolution.y <= 0 ||
                !Finite(stamp.AcquiredAtRealtimeSeconds) || stamp.AcquiredAtRealtimeSeconds < 0) return false;
            var p = stamp.Pose.position;
            var q = stamp.Pose.rotation;
            if (!Finite(p.x) || !Finite(p.y) || !Finite(p.z) || !Finite(q.x) || !Finite(q.y) || !Finite(q.z) || !Finite(q.w)) return false;
            var squaredLength = (double)q.x * q.x + (double)q.y * q.y + (double)q.z * q.z + (double)q.w * q.w;
            return Finite(squaredLength) && squaredLength > 1e-12;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
