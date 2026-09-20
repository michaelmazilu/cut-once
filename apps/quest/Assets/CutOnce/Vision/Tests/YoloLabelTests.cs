using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class YoloLabelTests
    {
        [TestCase("diningtable", "dining table")]
        [TestCase("tvmonitor", "TV / monitor")]
        [TestCase("pottedplant", "potted plant")]
        [TestCase(" bottle\r", "bottle")]
        [TestCase("person", "person")]
        [TestCase("", "object")]
        [TestCase(null, "object")]
        public void ModelLabelNamesAreReadableWithoutChangingTheirMeaning(string raw, string expected)
        {
            Assert.That(YoloDetector.NormalizeClassName(raw), Is.EqualTo(expected));
        }

        [Test]
        public void BundledTableLabelUsesTableSizedBoundsAfterNormalization()
        {
            var labels = Resources.Load<TextAsset>("SentisYoloClasses");
            Assert.That(labels, Is.Not.Null);
            var table = labels.text.Split('\n')[60];
            Assert.That(table.Trim(), Is.EqualTo("diningtable"));
            var displayName = YoloDetector.NormalizeClassName(table);
            var size = Object3DLocator.EstimateCameraSize(2.4f, 0.8f, displayName, 0.05f, 1.2f);
            Assert.That(size.x, Is.EqualTo(2.4f));
            Assert.That(size.z, Is.EqualTo(1.5f));
        }
    }
}
