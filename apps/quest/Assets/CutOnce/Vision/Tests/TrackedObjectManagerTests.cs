using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class TrackedObjectManagerTests
    {
        private GameObject _owner;
        private TrackedObjectManager _tracker;
        private readonly HashSet<int> _observed = new();
        private static readonly Vector3 BottleSize = new Vector3(0.07f, 0.25f, 0.07f);
        private static readonly DetectedObject Bottle = new DetectedObject
        {
            classId = 39,
            className = "bottle",
            confidence = 0.9f,
        };

        [SetUp]
        public void SetUp()
        {
            _owner = new GameObject("Tracker test");
            _tracker = _owner.AddComponent<TrackedObjectManager>();
            // Fixed radius makes these tests independent of cameras in the open Editor scene.
            _tracker.associationPerMetre = 0f;
            _tracker.associationDistance = 0.25f;
            _tracker.positionSmoothing = 1f;
            _observed.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            if (_owner != null) Object.DestroyImmediate(_owner);
        }

        [Test]
        public void RepeatedSightingsAcrossBatchesKeepOneIdentity()
        {
            var first = ObserveBottle(new Vector3(0f, 1f, 2f));
            for (var frame = 1; frame <= 8; frame++)
            {
                _observed.Clear();
                var jitter = frame % 2 == 0 ? 0.01f : -0.01f;
                var again = ObserveBottle(new Vector3(jitter, 1f, 2f));
                Assert.That(again, Is.SameAs(first));
                _tracker.EndFrame(_observed);
            }

            Assert.That(_tracker.Objects.Count, Is.EqualTo(1));
            Assert.That(first.totalHits, Is.EqualTo(9));
            Assert.That(first.visible, Is.True);
        }

        [Test]
        public void NearbySameClassObjectsStaySeparateAndSurviveReversedDetectionOrder()
        {
            var leftPosition = new Vector3(0f, 1f, 2f);
            var rightPosition = new Vector3(0.12f, 1f, 2f);
            var left = ObserveBottle(leftPosition);
            var right = ObserveBottle(rightPosition);
            Assert.That(right.id, Is.Not.EqualTo(left.id));
            Assert.That(_tracker.Objects.Count, Is.EqualTo(2));
            _tracker.EndFrame(_observed);

            for (var frame = 0; frame < 5; frame++)
            {
                _observed.Clear();
                Assert.That(ObserveBottle(rightPosition + Vector3.right * 0.005f), Is.SameAs(right));
                Assert.That(ObserveBottle(leftPosition + Vector3.right * 0.005f), Is.SameAs(left));
                _tracker.EndFrame(_observed);
            }

            Assert.That(_tracker.Objects.Count, Is.EqualTo(2), "Repeated detections must not accumulate duplicate tracks.");
            Assert.That(left.totalHits, Is.EqualTo(6));
            Assert.That(right.totalHits, Is.EqualTo(6));
            Assert.That(left.visible && right.visible, Is.True);
        }

        [Test]
        public void MissingNeighborDoesNotStealTheNearestRemainingIdentity()
        {
            var left = ObserveBottle(new Vector3(0f, 1f, 2f));
            var right = ObserveBottle(new Vector3(0.12f, 1f, 2f));
            _tracker.EndFrame(_observed);
            _observed.Clear();

            var remaining = ObserveBottle(new Vector3(0.125f, 1f, 2f));
            _tracker.EndFrame(_observed);

            Assert.That(remaining, Is.SameAs(right));
            Assert.That(left.consecutiveHits, Is.Zero);
            Assert.That(right.consecutiveHits, Is.EqualTo(2));
            Assert.That(_tracker.Objects.Count, Is.EqualTo(2), "A briefly unseen object stays tracked until its timeout.");
        }

        [Test]
        public void DifferentClassesCannotReuseOneTrackAtTheSamePosition()
        {
            var position = new Vector3(0f, 1f, 2f);
            var bottle = ObserveBottle(position);
            _observed.Clear();
            var cupDetection = new DetectedObject { classId = 41, className = "cup", confidence = 0.9f };
            var cup = _tracker.Observe(cupDetection, position, BottleSize, _observed);
            Assert.That(cup.id, Is.Not.EqualTo(bottle.id));
            Assert.That(_tracker.Objects.Count, Is.EqualTo(2));
        }

        [Test]
        public void RejectedJumpDoesNotConfirmOrRefreshAnyAcceptedMeasurement()
        {
            _tracker.associationDistance = 0.75f; // The jump gate must be inside association reach.
            var position = new Vector3(0f, 1f, 2f);
            var tracked = ObserveBottle(position);
            _observed.Clear();
            Assert.That(ObserveBottle(position), Is.SameAs(tracked));
            Assert.That(tracked.consecutiveHits, Is.EqualTo(2));
            tracked.lastSeenTime = Time.time - 1f;
            var acceptedAt = tracked.lastSeenTime;
            var badMeasurement = new DetectedObject { classId = 39, className = "untrusted label", confidence = 1f };

            _observed.Clear();
            var rejected = _tracker.Observe(badMeasurement, position + Vector3.right * 0.6f, Vector3.one * 5f, _observed);
            _observed.Add(rejected.id); // Real caller reserves the returned association even on rejection.
            _tracker.EndFrame(_observed);

            Assert.That(rejected, Is.SameAs(tracked));
            Assert.That(_tracker.Objects.Count, Is.EqualTo(1));
            Assert.That(tracked.visible, Is.False, "A rejected third sighting must not promote stale geometry.");
            Assert.That(tracked.consecutiveHits, Is.Zero);
            Assert.That(tracked.totalHits, Is.EqualTo(2));
            Assert.That(tracked.lastSeenTime, Is.EqualTo(acceptedAt));
            Assert.That(tracked.className, Is.EqualTo("bottle"));
            Assert.That(tracked.confidence, Is.EqualTo(Bottle.confidence));
            Assert.That(tracked.worldPosition, Is.EqualTo(position));
            Assert.That(tracked.smoothedWorldPosition, Is.EqualTo(position));
            Assert.That(tracked.worldSize, Is.EqualTo(BottleSize));
            Assert.That(tracked.smoothedWorldSize, Is.EqualTo(BottleSize));
        }

        [Test]
        public void RepeatedRejectedJumpsDoNotKeepAConfirmedObjectAlive()
        {
            _tracker.associationDistance = 0.75f;
            var position = new Vector3(0f, 1f, 2f);
            var tracked = ObserveBottle(position);
            for (var frame = 0; frame < 2; frame++) { _observed.Clear(); ObserveBottle(position); }
            Assert.That(tracked.visible, Is.True);

            // A temporarily unseen confirmed object still keeps the ordinary keep-alive window.
            tracked.lastSeenTime = Time.time - _tracker.keepAliveSeconds * 0.5f;
            Assert.That(_tracker.Prune().Count, Is.Zero);
            Assert.That(_tracker.VisibleCount, Is.EqualTo(1));
            tracked.lastSeenTime = Time.time - _tracker.keepAliveSeconds - 0.1f;
            var acceptedAt = tracked.lastSeenTime;
            for (var frame = 0; frame < 4; frame++)
            {
                _observed.Clear();
                Assert.That(ObserveBottle(position + Vector3.right * 0.6f), Is.SameAs(tracked));
                _tracker.EndFrame(_observed);
            }

            Assert.That(tracked.lastSeenTime, Is.EqualTo(acceptedAt));
            Assert.That(tracked.totalHits, Is.EqualTo(3));
            var removed = _tracker.Prune();
            Assert.That(removed, Has.Count.EqualTo(1));
            Assert.That(removed[0], Is.SameAs(tracked));
            Assert.That(_tracker.Objects, Is.Empty);
            Assert.That(_tracker.VisibleCount, Is.Zero);
        }

        [Test]
        public void ValidObservationAfterARejectedJumpRefreshesAndStartsANewConfirmationStreak()
        {
            _tracker.associationDistance = 0.75f;
            var position = new Vector3(0f, 1f, 2f);
            var tracked = ObserveBottle(position);
            _observed.Clear();
            ObserveBottle(position);
            _observed.Clear();
            ObserveBottle(position + Vector3.right * 0.6f);
            tracked.lastSeenTime = Time.time - 1f;
            var nextPosition = position + Vector3.right * 0.02f;
            var nextSize = BottleSize * 1.1f;

            _observed.Clear();
            var accepted = _tracker.Observe(Bottle, nextPosition, nextSize, _observed);
            _observed.Add(accepted.id);
            _tracker.EndFrame(_observed);

            Assert.That(accepted, Is.SameAs(tracked));
            Assert.That(tracked.lastSeenTime, Is.EqualTo(Time.time));
            Assert.That(tracked.totalHits, Is.EqualTo(3), "Only the three valid observations count.");
            Assert.That(tracked.consecutiveHits, Is.EqualTo(1));
            Assert.That(tracked.visible, Is.False);
            Assert.That(tracked.worldPosition, Is.EqualTo(nextPosition));
            Assert.That(tracked.smoothedWorldPosition, Is.EqualTo(nextPosition));
            Assert.That(tracked.worldSize, Is.EqualTo(nextSize));
            Assert.That(tracked.smoothedWorldSize, Is.EqualTo(nextSize));
            for (var frame = 0; frame < 2; frame++) { _observed.Clear(); ObserveBottle(nextPosition); }
            Assert.That(tracked.visible, Is.True, "Subsequent valid nearby observations still promote the same object.");
            Assert.That(_tracker.Objects, Has.Count.EqualTo(1));
        }

        private TrackedObject ObserveBottle(Vector3 position)
        {
            var tracked = _tracker.Observe(Bottle, position, BottleSize, _observed);
            _observed.Add(tracked.id);
            return tracked;
        }

        [Test]
        public void LiveObservationKeepsAcquisitionTimeRatherThanResultArrivalTime()
        {
            var acquiredAt = Time.realtimeSinceStartupAsDouble - .4d;
            var tracked = _tracker.Observe(Bottle, new Vector3(0f, 1f, 2f), BottleSize, null, new DetectionFrameTiming(acquiredAt));
            Assert.That(tracked.lastAcquiredAtRealtimeSeconds, Is.EqualTo(acquiredAt));
            Assert.That(tracked.AgeAt(acquiredAt + .45d, Time.time), Is.EqualTo(.45f).Within(.0001f));
        }

        [TestCase(0d)]
        [TestCase(-.1d)]
        public void RepeatedOrOlderLiveFramesDoNotRefreshOrReconfirmTheTrack(double offset)
        {
            var position = new Vector3(0f, 1f, 2f);
            var acquiredAt = Time.realtimeSinceStartupAsDouble - .3d;
            var tracked = _tracker.Observe(Bottle, position, BottleSize, null, new DetectionFrameTiming(acquiredAt));
            var legacyEstimate = tracked.lastSeenTime;

            var repeated = _tracker.Observe(Bottle, position + Vector3.right * .02f, BottleSize * 2f, null,
                new DetectionFrameTiming(acquiredAt + offset));

            Assert.That(repeated, Is.SameAs(tracked));
            Assert.That(tracked.totalHits, Is.EqualTo(1));
            Assert.That(tracked.consecutiveHits, Is.EqualTo(1));
            Assert.That(tracked.lastAcquiredAtRealtimeSeconds, Is.EqualTo(acquiredAt));
            Assert.That(tracked.lastSeenTime, Is.EqualTo(legacyEstimate));
            Assert.That(tracked.worldPosition, Is.EqualTo(position));
            Assert.That(tracked.worldSize, Is.EqualTo(BottleSize));
        }

        [Test]
        public void RejectedDepthJumpDoesNotRefreshTheLiveAcquisitionClock()
        {
            _tracker.associationDistance = .75f;
            var position = new Vector3(0f, 1f, 2f);
            var acquiredAt = Time.realtimeSinceStartupAsDouble - .3d;
            var tracked = _tracker.Observe(Bottle, position, BottleSize, null, new DetectionFrameTiming(acquiredAt));
            _tracker.Observe(Bottle, position + Vector3.right * .6f, BottleSize * 2f, null,
                new DetectionFrameTiming(acquiredAt + .2d));
            Assert.That(tracked.lastAcquiredAtRealtimeSeconds, Is.EqualTo(acquiredAt));
            Assert.That(tracked.totalHits, Is.EqualTo(1));
        }

        [Test]
        public void ANewerAcceptedLiveFrameUpdatesTheSameTrackWithItsOwnCaptureTime()
        {
            var position = new Vector3(0f, 1f, 2f);
            var acquiredAt = Time.realtimeSinceStartupAsDouble - .3d;
            var tracked = _tracker.Observe(Bottle, position, BottleSize, null, new DetectionFrameTiming(acquiredAt));
            var updated = _tracker.Observe(Bottle, position + Vector3.right * .02f, BottleSize, null,
                new DetectionFrameTiming(acquiredAt + .2d));
            Assert.That(updated, Is.SameAs(tracked));
            Assert.That(tracked.lastAcquiredAtRealtimeSeconds, Is.EqualTo(acquiredAt + .2d));
            Assert.That(tracked.totalHits, Is.EqualTo(2));
        }

        [Test]
        public void PruningUsesLiveCaptureAgeEvenWhenLegacyArrivalTimeLooksFresh()
        {
            var acquiredAt = Time.realtimeSinceStartupAsDouble - .1d;
            var tracked = _tracker.Observe(Bottle, new Vector3(0f, 1f, 2f), BottleSize, null, new DetectionFrameTiming(acquiredAt));
            tracked.lastAcquiredAtRealtimeSeconds = Time.realtimeSinceStartupAsDouble - _tracker.keepAliveSeconds - .1d;
            tracked.lastSeenTime = Time.time;

            var removed = _tracker.Prune();
            Assert.That(removed, Has.Count.EqualTo(1));
            Assert.That(removed[0], Is.SameAs(tracked));
            Assert.That(_tracker.Objects, Is.Empty);
        }
    }
}
