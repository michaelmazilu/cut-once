using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class ContinuousTrackingTests
    {
        private GameObject _gameObject;

        [TearDown]
        public void TearDown()
        {
            if (_gameObject != null) Object.DestroyImmediate(_gameObject);
        }

        [Test]
        public void DetectorTargetsFifteenFreshFramesPerSecond()
        {
            _gameObject = new GameObject("detector test");
            var detector = _gameObject.AddComponent<YoloDetector>();

            Assert.That(detector.maxInferencesPerSecond,
                Is.EqualTo(YoloDetector.DefaultMaxInferencesPerSecond));
            Assert.That(detector.maxInferencesPerSecond, Is.EqualTo(15f));
        }

        [Test]
        public void EveryTrackedObjectUpdatesItsPositionAndBoxSizeOnLaterFrames()
        {
            _gameObject = new GameObject("tracker test");
            var tracker = _gameObject.AddComponent<TrackedObjectManager>();
            tracker.hitsBeforeVisible = 1;
            tracker.positionSmoothing = 1f;
            tracker.jumpRejectDistance = 2f;

            var chairDetection = Detection(56, "chair");
            var bottleDetection = Detection(39, "bottle");
            var chair = tracker.Observe(chairDetection, new Vector3(0f, 0.8f, 1f), new Vector3(.5f, .8f, .5f));
            var bottle = tracker.Observe(bottleDetection, new Vector3(1f, 0.8f, 1f), new Vector3(.1f, .3f, .1f));

            var updatedChairPosition = new Vector3(.08f, .8f, 1.02f);
            var updatedChairSize = new Vector3(.55f, .82f, .52f);
            var updatedBottlePosition = new Vector3(1.05f, .82f, 1.01f);
            var updatedBottleSize = new Vector3(.11f, .31f, .1f);

            Assert.That(tracker.Observe(chairDetection, updatedChairPosition, updatedChairSize), Is.SameAs(chair));
            Assert.That(tracker.Observe(bottleDetection, updatedBottlePosition, updatedBottleSize), Is.SameAs(bottle));

            Assert.That(tracker.Objects, Has.Count.EqualTo(2));
            Assert.That(chair.smoothedWorldPosition, Is.EqualTo(updatedChairPosition));
            Assert.That(chair.smoothedWorldSize, Is.EqualTo(updatedChairSize));
            Assert.That(bottle.smoothedWorldPosition, Is.EqualTo(updatedBottlePosition));
            Assert.That(bottle.smoothedWorldSize, Is.EqualTo(updatedBottleSize));
            Assert.That(chair.visible, Is.True);
            Assert.That(bottle.visible, Is.True);
        }

        private static DetectedObject Detection(int classId, string className) => new DetectedObject
        {
            classId = classId,
            className = className,
            confidence = .9f,
            boundingBox = new Rect(100f, 100f, 100f, 100f),
            centerPixel = new Vector2(150f, 150f),
            inputSize = new Vector2(640f, 640f),
        };
    }
}
