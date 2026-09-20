using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class DetectionFreshnessTests
    {
        [TestCase(10.1d, DetectionPublicationRejection.None)]
        [TestCase(10.5d, DetectionPublicationRejection.None)]
        [TestCase(10.50001d, DetectionPublicationRejection.StaleLiveFrame)]
        [TestCase(10.8d, DetectionPublicationRejection.StaleLiveFrame)]
        public void LiveResultsMustFitTheConfiguredAcquisitionAgeWindow(double completedAt, DetectionPublicationRejection expected)
        {
            var frame = new DetectionFrameTiming(10d);
            Assert.That(DetectionPublicationGate.Evaluate(frame, completedAt, .5d, 0, 0, false), Is.EqualTo(expected));
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        [TestCase(-1d)]
        [TestCase(11d)]
        public void InvalidOrFutureAcquisitionTimesAreNotInventedAsFresh(double acquiredAt)
        {
            Assert.That(DetectionPublicationGate.Evaluate(new DetectionFrameTiming(acquiredAt), 10d, .5d, 0, 0, false),
                Is.EqualTo(DetectionPublicationRejection.InvalidLiveTimestamp));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void PausingInFlightPreventsPublicationForLiveAndRecordedCalls(bool live)
        {
            var frame = live ? new DetectionFrameTiming(10d) : default;
            Assert.That(DetectionPublicationGate.Evaluate(frame, 10.1d, .5d, 0, 1, true),
                Is.EqualTo(DetectionPublicationRejection.PausedOrSuperseded));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void PauseThenResumeStillRejectsTheOldInFlightGeneration(bool live)
        {
            var frame = live ? new DetectionFrameTiming(10d) : default;
            Assert.That(DetectionPublicationGate.Evaluate(frame, 10.1d, .5d, 0, 2, false),
                Is.EqualTo(DetectionPublicationRejection.PausedOrSuperseded));
            Assert.That(DetectionPublicationGate.Evaluate(frame, 10.1d, .5d, 2, 2, false),
                Is.EqualTo(DetectionPublicationRejection.None), "A new frame after resume is permitted.");
        }

        [Test]
        public void RecordedPhotosDoNotPretendToBeLiveOrFailTheLiveAgeLimit()
        {
            DetectionFrameTiming recorded = default;
            Assert.That(recorded.isLiveCapture, Is.False);
            Assert.That(DetectionPublicationGate.Evaluate(recorded, 1000d, .5d, 0, 0, false),
                Is.EqualTo(DetectionPublicationRejection.None));
        }

        [Test]
        public void DefaultLiveBudgetMatchesSurfaceFreshWindowWithoutChangingConfidence()
        {
            var owner = new GameObject("Detector freshness default test");
            try
            {
                var detector = owner.AddComponent<YoloDetector>();
                Assert.That(detector.maxLiveResultAgeSeconds, Is.EqualTo(ObjectVisualizer.SurfaceFadeStartsAfterSeconds));
                Assert.That(detector.scoreThreshold, Is.EqualTo(.35f));
            }
            finally { Object.DestroyImmediate(owner); }
        }

        [Test]
        public void LiveAgingUsesAcquisitionTimeAndIgnoresScaledTimeChanges()
        {
            var tracked = new TrackedObject { lastAcquiredAtRealtimeSeconds = 10d, lastSeenTime = 999f };
            Assert.That(tracked.AgeAt(10.4d, 999f), Is.EqualTo(.4f).Within(.0001f));
            Assert.That(tracked.AgeAt(10.4d, 0f), Is.EqualTo(.4f).Within(.0001f));
            Assert.That(tracked.AgeAt(10.4d, 99999f), Is.EqualTo(.4f).Within(.0001f));
        }

        [Test]
        public void DelayedResultsDoNotRestartTheSurfaceFadeClockAtPublication()
        {
            // Acquired at 10.0, published at 10.4: at 10.6 this is already in the fade window,
            // not a fresh 0.2-second-old object. At 10.8 the surface must be fully hidden.
            var tracked = new TrackedObject { lastAcquiredAtRealtimeSeconds = 10d, lastSeenTime = 100f };
            Assert.That(ObjectVisualizer.SurfaceFreshnessAtAge(tracked.AgeAt(10.6d, 100.2f)), Is.EqualTo(.6f).Within(.0001f));
            Assert.That(ObjectVisualizer.SurfaceFreshnessAtAge(tracked.AgeAt(10.8d, 100.4f)), Is.Zero);
        }

        [Test]
        public void LegacyTrackCallersRetainTheirExistingScaledTimeContract()
        {
            var tracked = new TrackedObject { lastSeenTime = 4f };
            Assert.That(tracked.AgeAt(1000d, 4.25f), Is.EqualTo(.25f));
            Assert.That(tracked.AgeAt(10000d, 4.25f), Is.EqualTo(.25f));
        }
    }
}
