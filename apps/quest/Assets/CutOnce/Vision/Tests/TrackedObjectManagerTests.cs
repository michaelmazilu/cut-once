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

        private TrackedObject ObserveBottle(Vector3 position)
        {
            var tracked = _tracker.Observe(Bottle, position, BottleSize, _observed);
            _observed.Add(tracked.id);
            return tracked;
        }
    }
}
