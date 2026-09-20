using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class YoloBoxLayoutTests
    {
        [Test]
        public void DetectorDefaultsToBundledModelsBakedCornerOutput()
        {
            var go = new GameObject("YOLO layout test");
            try
            {
                var detector = go.AddComponent<YoloDetector>();
                Assert.That(detector.cornerBoxes, Is.True,
                    "The bundled Meta model already converts centre+size to corners in its graph.");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [TestCase(270f, 100f, 370f, 500f)]
        [TestCase(10f, 20f, 100f, 150f)]
        [TestCase(-5f, 0f, 645f, 640f)]
        public void BakedCornersPassThroughWithoutASecondConversion(float x1, float y1, float x2, float y2)
        {
            var raw = new Vector4(x1, y1, x2, y2);
            Assert.That(YoloDetector.DecodeModelBox(raw), Is.EqualTo(raw));
            Assert.That(YoloDetector.DecodeModelBox(raw, corners: true), Is.EqualTo(raw));
        }

        [Test]
        public void BottleCornersPreserveThePixelCentreUsedForDepthRays()
        {
            var box = YoloDetector.DecodeModelBox(new Vector4(270f, 100f, 370f, 500f));
            var rect = new Rect(box.x, box.y, box.z - box.x, box.w - box.y);
            Assert.That(rect.center, Is.EqualTo(new Vector2(320f, 300f)));
            Assert.That(rect.size, Is.EqualTo(new Vector2(100f, 400f)));
            Assert.That(new Vector2(rect.center.x / 640f, 1f - rect.center.y / 640f),
                Is.EqualTo(new Vector2(0.5f, 0.53125f)));
        }

        [Test]
        public void RawCentreSizeRequiresExplicitReplacementModelOptIn()
        {
            var raw = new Vector4(320f, 300f, 100f, 400f);
            Assert.That(YoloDetector.DecodeModelBox(raw, corners: false),
                Is.EqualTo(new Vector4(270f, 100f, 370f, 500f)));
        }
    }
}
