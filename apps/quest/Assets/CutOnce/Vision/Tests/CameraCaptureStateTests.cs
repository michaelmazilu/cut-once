using System;
using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class CameraCaptureStateTests
    {
        private static CameraCaptureStamp Stamp(double acquired = 10d, int generation = 3)
            => new CameraCaptureStamp(new Pose(new Vector3(1, 2, 3), Quaternion.identity),
                new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc), new Vector2Int(1280, 960), generation, acquired);

        [Test]
        public void OnePendingRequestCannotBeReplacedOrRelabelled()
        {
            var state = new CameraCaptureState();
            Assert.That(state.TryBegin(Stamp()), Is.True);
            Assert.That(state.TryBegin(Stamp(20, 99)), Is.False);
            Assert.That(state.Status, Is.EqualTo(CameraSnapshotStatus.Pending));
            Assert.That(state.Stamp.AcquiredAtRealtimeSeconds, Is.EqualTo(10));
            Assert.That(state.Stamp.Generation, Is.EqualTo(3));
            Assert.That(state.Complete(true, 10.1, 3, .5), Is.True);
            Assert.That(state.LastRejection, Is.EqualTo(CameraSnapshotRejection.None), "A refused busy caller cannot corrupt the accepted request.");
        }

        [Test]
        public void ReadyAndLeasedPixelsStayOwnedUntilExplicitRelease()
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            Assert.That(state.Complete(true, 10.1, 3, .5), Is.True);
            Assert.That(state.TryBegin(Stamp(10.2)), Is.False);
            Assert.That(state.TryTake(), Is.True);
            Assert.That(state.TryTake(), Is.False);
            Assert.That(state.TryBegin(Stamp(10.3)), Is.False);
            state.Release();
            Assert.That(state.TryBegin(Stamp(10.4)), Is.True);
        }

        [TestCase(10d, true)]
        [TestCase(10.499d, true)]
        [TestCase(10.5d, true)]
        [TestCase(10.500001d, false)]
        [TestCase(11d, false)]
        public void ReadbackTimeConsumesTheOriginalHalfSecondWindow(double completedAt, bool accepted)
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            Assert.That(state.Complete(true, completedAt, 3, .5), Is.EqualTo(accepted));
            Assert.That(state.Stamp.AcquiredAtRealtimeSeconds, Is.EqualTo(10), "Completion must not restart the freshness clock.");
            if (!accepted) Assert.That(state.LastRejection, Is.EqualTo(CameraSnapshotRejection.Stale));
        }

        [Test]
        public void TimeoutNeverReusesSlotBeforeOutstandingRequestDrains()
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            state.Check(10.6, 3, .5);
            Assert.That(state.Status, Is.EqualTo(CameraSnapshotStatus.Draining));
            Assert.That(state.TryBegin(Stamp(20)), Is.False);
            Assert.That(state.LastRejection, Is.EqualTo(CameraSnapshotRejection.Stale));
            Assert.That(state.Complete(true, 20, 3, .5), Is.False);
            Assert.That(state.Status, Is.EqualTo(CameraSnapshotStatus.Idle));
            Assert.That(state.TryTake(), Is.False);
            Assert.That(state.TryBegin(Stamp(20.1)), Is.True);
        }

        [Test]
        public void PausingAndResumingCannotPublishARequestFromTheOldGeneration()
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            state.Check(10.1, 4, .5); // pause
            Assert.That(state.Complete(true, 10.2, 5, .5), Is.False); // resume
            Assert.That(state.LastRejection, Is.EqualTo(CameraSnapshotRejection.GenerationChanged));
            Assert.That(state.TryTake(), Is.False);
            Assert.That(state.TryBegin(Stamp(10.3, 5)), Is.True);
            Assert.That(state.Complete(true, 10.4, 5, .5), Is.True);
        }

        [Test]
        public void MetadataInconsistencyInvalidatesWithoutCancellingOrReusingRequest()
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            state.Invalidate(CameraSnapshotRejection.MetadataChanged);
            Assert.That(state.Status, Is.EqualTo(CameraSnapshotStatus.Draining));
            Assert.That(state.Complete(true, 10.1, 3, .5), Is.False);
            Assert.That(state.LastRejection, Is.EqualTo(CameraSnapshotRejection.MetadataChanged));
        }

        [Test]
        public void AReadySnapshotCanExpireBeforeItsConsumerTakesIt()
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            state.Complete(true, 10.1, 3, .5);
            state.Check(10.6, 3, .5);
            Assert.That(state.Status, Is.EqualTo(CameraSnapshotStatus.Idle));
            Assert.That(state.TryTake(), Is.False);
        }

        [Test]
        public void RestartDoesNotOverwriteOrDestroyAnAlreadyLeasedSnapshot()
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            state.Complete(true, 10.1, 3, .5);
            state.TryTake();
            state.Check(10.2, 4, .5);
            Assert.That(state.Status, Is.EqualTo(CameraSnapshotStatus.Leased));
            Assert.That(state.TryBegin(Stamp(10.3, 4)), Is.False);
            Assert.That(state.Stamp.Generation, Is.EqualTo(3));
            state.Release();
            Assert.That(state.TryBegin(Stamp(10.3, 4)), Is.True);
        }

        [Test]
        public void ReadbackFailureHasNoFallbackPixelsAndAllowsNextRequestAfterCompletion()
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            Assert.That(state.Complete(false, 10.1, 3, .5), Is.False);
            Assert.That(state.LastRejection, Is.EqualTo(CameraSnapshotRejection.ReadbackFailed));
            Assert.That(state.TryTake(), Is.False);
            Assert.That(state.TryBegin(Stamp(10.2)), Is.True);
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        [TestCase(-1d)]
        public void InvalidAcquisitionTimeCannotStartARequest(double acquired)
        {
            var state = new CameraCaptureState();
            Assert.That(state.TryBegin(Stamp(acquired)), Is.False);
            Assert.That(state.Status, Is.EqualTo(CameraSnapshotStatus.Idle));
        }

        [TestCase(double.NaN, .5)]
        [TestCase(9.9, .5)]
        [TestCase(10.1, 0)]
        [TestCase(10.1, double.NaN)]
        public void InvalidClockOrBudgetCannotMakePixelsFresh(double now, double budget)
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            Assert.That(state.Complete(true, now, 3, budget), Is.False);
            Assert.That(state.LastRejection, Is.EqualTo(CameraSnapshotRejection.InvalidMetadata));
        }

        [Test]
        public void InvalidPoseResolutionAndDefaultTimestampAreRejected()
        {
            var valid = Stamp();
            var state = new CameraCaptureState();
            Assert.That(state.TryBegin(default), Is.False);
            Assert.That(state.TryBegin(new CameraCaptureStamp(default, valid.CameraTimestamp, valid.Resolution, 3, 10)), Is.False);
            Assert.That(state.TryBegin(new CameraCaptureStamp(valid.Pose, default, valid.Resolution, 3, 10)), Is.False);
            Assert.That(state.TryBegin(new CameraCaptureStamp(valid.Pose, valid.CameraTimestamp, Vector2Int.zero, 3, 10)), Is.False);
            var invalidPose = new Pose(new Vector3(float.NaN, 0, 0), Quaternion.identity);
            Assert.That(state.TryBegin(new CameraCaptureStamp(invalidPose, valid.CameraTimestamp, valid.Resolution, 3, 10)), Is.False);
        }

        [Test]
        public void CapturedMetadataIsValueCopiedIncludingProjection()
        {
            var valid = Stamp();
            Assert.That(CapturedCameraProjection.TryCreate(valid.Pose, new Vector2(1000, 1000), new Vector2(640, 480),
                valid.Resolution, valid.Resolution, out var projection), Is.True);
            var stamp = new CameraCaptureStamp(valid.Pose, valid.CameraTimestamp, valid.Resolution, 3, 10, projection);
            var state = new CameraCaptureState();
            state.TryBegin(stamp);
            Assert.That(state.Stamp.Pose.position, Is.EqualTo(valid.Pose.position));
            Assert.That(state.Stamp.CameraTimestamp, Is.EqualTo(valid.CameraTimestamp));
            Assert.That(state.Stamp.Projection.IsValid, Is.True);
            Assert.That(state.Stamp.Projection.WorldToMask, Is.EqualTo(projection.WorldToMask));
        }

        [Test]
        public void DisposedOwnerCannotAcceptLateCompletionOrRestart()
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            state.Dispose();
            Assert.That(state.Complete(true, 10.1, 3, .5), Is.False);
            Assert.That(state.TryTake(), Is.False);
            state.Release();
            Assert.That(state.TryBegin(Stamp(10.2)), Is.False);
            Assert.That(state.Status, Is.EqualTo(CameraSnapshotStatus.Disposed));
        }

        [Test]
        public void OnlyExplicitDiagnosticCallerCanRequestLongerNumericFixtureWindow()
        {
            var state = new CameraCaptureState();
            state.TryBegin(Stamp());
            Assert.That(state.Complete(true, 12, 3, 60), Is.True);
            // Production VisionCamera never uses this fixture override: it always supplies .5.
        }
    }
}
