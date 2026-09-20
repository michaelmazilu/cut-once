using NUnit.Framework;

namespace CutOnce.Vision.Tests
{
    public sealed class YoloPublicationCadenceTests
    {
        [Test]
        public void FirstSuccessfulPublicationDoesNotPretendToMeasureARate()
        {
            var cadence = new YoloDetector.PublicationCadence();
            Assert.That(cadence.Read(10d), Is.Zero);
            cadence.Record(10d);
            Assert.That(cadence.Read(10d), Is.Zero);
        }

        [Test]
        public void EightHzPublicationsReportEightRegardlessOfFastModelLatency()
        {
            var cadence = new YoloDetector.PublicationCadence();
            // A model may finish in 20 ms (50/s capacity), but the scanner deliberately publishes
            // every 125 ms. Only real publication timestamps enter this measurement.
            for (var index = 0; index <= 16; index++) cadence.Record(100d + index / 8d);
            Assert.That(cadence.Read(102d), Is.EqualTo(8f).Within(.0001f));
        }

        [Test]
        public void CameraWaitsAndRateLimitingAreIncludedBetweenPublications()
        {
            var cadence = new YoloDetector.PublicationCadence();
            cadence.Record(10d);
            cadence.Record(10.5d);
            Assert.That(cadence.Read(10.5d), Is.EqualTo(2f).Within(.0001f));
        }

        [Test]
        public void IntervalSmoothingIsSeparateFromProcessingLatency()
        {
            var cadence = new YoloDetector.PublicationCadence();
            cadence.Record(10d);
            cadence.Record(10.125d);
            cadence.Record(10.375d);
            Assert.That(cadence.Read(10.375d), Is.EqualTo(1f / .15f).Within(.0001f));
        }

        [Test]
        public void PauseResetRequiresTwoFreshPublicationsAfterResuming()
        {
            var cadence = new YoloDetector.PublicationCadence();
            cadence.Record(10d);
            cadence.Record(10.125d);
            cadence.Reset(); // The detector's Paused setter calls this on each pause/resume transition.
            Assert.That(cadence.Read(10.15d), Is.Zero);
            cadence.Record(10.2d);
            Assert.That(cadence.Read(10.2d), Is.Zero);
            cadence.Record(10.325d);
            Assert.That(cadence.Read(10.325d), Is.EqualTo(8f).Within(.0001f));
        }

        [Test]
        public void ReadingAfterALongGapDropsTheOldRate()
        {
            var cadence = new YoloDetector.PublicationCadence();
            cadence.Record(10d);
            cadence.Record(10.125d);
            Assert.That(cadence.Read(10.126d + YoloDetector.PublicationCadence.ResetGapSeconds), Is.Zero);
            cadence.Record(13d);
            Assert.That(cadence.Read(13d), Is.Zero);
            cadence.Record(13.125d);
            Assert.That(cadence.Read(13.125d), Is.EqualTo(8f).Within(.0001f));
        }

        [Test]
        public void PublicationAfterALongGapAlsoResetsWithoutAnInterveningRead()
        {
            var cadence = new YoloDetector.PublicationCadence();
            cadence.Record(10d);
            cadence.Record(10.125d);
            cadence.Record(20d);
            Assert.That(cadence.Read(20d), Is.Zero);
            cadence.Record(20.25d);
            Assert.That(cadence.Read(20.25d), Is.EqualTo(4f).Within(.0001f));
        }

        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NegativeInfinity)]
        [TestCase(-1d)]
        [TestCase(9d)]
        [TestCase(10.125d)]
        public void InvalidBackwardsOrDuplicatePublicationClocksNeverProduceAFakeRate(double invalid)
        {
            var cadence = new YoloDetector.PublicationCadence();
            cadence.Record(10d);
            cadence.Record(10.125d);
            cadence.Record(invalid);
            Assert.That(cadence.Read(invalid), Is.Zero);
        }
    }
}
