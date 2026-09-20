using System.Collections.Generic;
using CutOnce.Core.Vision;
using CutOnce.Vision;
using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Device.PlayTests
{
    /// <summary>
    /// The seam between detecting a thing and measuring it. Detection is per-frame and cheap; a measured box costs
    /// a few hundred raycasts and happens a few times a second, so the two run at different rates and the tracker
    /// holds the difference. These are the rules that seam has to keep.
    /// </summary>
    public class VisionTrackingTests
    {
        TrackedObjectManager _tracker;
        GameObject _host;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("tracker");
            _tracker = _host.AddComponent<TrackedObjectManager>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);
        }

        static DetectedObject Detection(string className = "bottle", int classId = 39) => new DetectedObject
        {
            classId = classId, className = className, confidence = 0.9f,
            boundingBox = new Rect(100f, 100f, 80f, 200f), inputSize = new Vector2(640f, 640f),
        };

        /// <summary>The old estimate: a detection rectangle and one distance, which is what used to be drawn.</summary>
        static readonly Vector3 Guess = new Vector3(0.4f, 0.9f, 0.4f);

        [Test]
        public void UntilItIsMeasuredThereIsNoBox()
        {
            var o = _tracker.Observe(Detection(), new Vector3(0f, 0.9f, 1f), Guess);
            Assert.That(o.hasMeasuredBox, Is.False, "a detection alone is not a measurement");
            Assert.That(o.DisplaySize, Is.EqualTo(Guess), "with nothing measured it can only offer the old estimate");
        }

        [Test]
        public void AMeasuredBoxReplacesTheEstimateEntirely()
        {
            var o = _tracker.Observe(Detection(), new Vector3(0f, 0.9f, 1f), Guess);
            var fit = FitResult.Fitted(new P3(0.1f, 0.86f, 1.05f), 12f, new P3(0.07f, 0.25f, 0.07f), 0.9f, 180);
            _tracker.Measure(o, fit);

            Assert.That(o.hasMeasuredBox, Is.True);
            Assert.That(o.DisplaySize.x, Is.EqualTo(0.07f).Within(1e-4f));
            Assert.That(o.DisplaySize.y, Is.EqualTo(0.25f).Within(1e-4f));
            Assert.That(o.DisplayYawDeg, Is.EqualTo(12f).Within(0.5f), "the turn must survive the trip");
            Assert.That(o.DisplayCentre.z, Is.EqualTo(1.05f).Within(1e-3f));
            Assert.That(o.geometryConfidence, Is.GreaterThan(0f));
        }

        [Test]
        public void ARejectedFitDrawsNothingRatherThanSomethingWrong()
        {
            var o = _tracker.Observe(Detection(), new Vector3(0f, 0.9f, 1f), Guess);
            _tracker.Measure(o, FitResult.Rejected("could not be told apart from the surface it lies on"));

            Assert.That(o.hasMeasuredBox, Is.False, "a refusal is not a box");
            Assert.That(o.DisplaySize, Is.EqualTo(Guess));
            Assert.That(o.lastFitReason, Does.Contain("surface"), "and it says why, for the debug label");
        }

        [Test]
        public void ARefusalAfterAGoodFitKeepsTheBoxButCostsConfidence()
        {
            var o = _tracker.Observe(Detection(), new Vector3(0f, 0.9f, 1f), Guess);
            _tracker.Measure(o, FitResult.Fitted(new P3(0f, 0.86f, 1f), 0f, new P3(0.07f, 0.25f, 0.07f), 1f, 200));
            var held = o.DisplaySize;
            var confident = o.geometryConfidence;

            for (var i = 0; i < 3; i++) _tracker.Measure(o, FitResult.Rejected("nothing to measure"));

            Assert.That(o.hasMeasuredBox, Is.True, "one bad look does not delete a box that was measured");
            Assert.That(o.DisplaySize, Is.EqualTo(held), "…and must not move it either");
            Assert.That(o.geometryConfidence, Is.LessThanOrEqualTo(confident), "but it should be trusted less");
        }

        [Test]
        public void ForgettingTheRoomDropsEverythingAndSaysWhatItDropped()
        {
            var a = _tracker.Observe(Detection("bottle", 39), new Vector3(0f, 0.9f, 1f), Guess);
            var b = _tracker.Observe(Detection("cup", 41), new Vector3(1.5f, 0.9f, 1f), Guess);
            Assert.That(_tracker.Objects.Count, Is.EqualTo(2));

            var dropped = _tracker.Forget(new List<TrackedObject>());

            Assert.That(_tracker.Objects.Count, Is.EqualTo(0), "nothing may survive a move");
            Assert.That(dropped, Has.Count.EqualTo(2), "and the caller must learn what to free, or the visuals leak");
            Assert.That(dropped, Contains.Item(a));
            Assert.That(dropped, Contains.Item(b));
            Assert.That(_tracker.VisibleCount, Is.EqualTo(0));
        }

        [Test]
        public void TwoSightingsOfOneThingStayOneThing()
        {
            var first = _tracker.Observe(Detection(), new Vector3(0f, 0.9f, 1f), Guess);
            var again = _tracker.Observe(Detection(), new Vector3(0.05f, 0.9f, 1.02f), Guess);
            Assert.That(again, Is.SameAs(first));
            Assert.That(_tracker.Objects.Count, Is.EqualTo(1));
        }

        [Test]
        public void TwoDifferentThingsStayTwo()
        {
            _tracker.Observe(Detection(), new Vector3(0f, 0.9f, 1f), Guess);
            _tracker.Observe(Detection(), new Vector3(0f, 0.9f, 3f), Guess);   // two metres away is another bottle
            Assert.That(_tracker.Objects.Count, Is.EqualTo(2));
        }

        [Test]
        public void PeopleAndFurnitureNeverEvenReachTheTracker()
        {
            // RoomScanner drops these before any depth is touched. The list is the model's own spelling, which is
            // what this really guards: `sofa`, not `couch`.
            foreach (var room in new[] { "person", "sofa", "diningtable", "tvmonitor", "pottedplant", "chair" })
                Assert.That(SizePriors.Ignored(room), Is.True, room);
            foreach (var material in new[] { "bottle", "cup", "book", "laptop" })
                Assert.That(SizePriors.Ignored(material), Is.False, material);
        }
    }
}
