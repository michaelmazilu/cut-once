using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class YoloLetterboxTests
    {
        [Test]
        public void LandscapeCameraFitsWithoutStretching()
        {
            var layout = YoloLetterboxLayout.Create(new Vector2Int(1280, 960), new Vector2Int(640, 640));
            Assert.That(layout.resizedSize, Is.EqualTo(new Vector2Int(640, 480)));
            Assert.That(layout.topLeftPadding, Is.EqualTo(new Vector2Int(0, 80)));
            Assert.That(layout.ContentUvRect, Is.EqualTo(new Vector4(0f, .125f, 1f, .75f)));
            Assert.That(layout.ToSourceRect(new Rect(100, 180, 100, 200)), Is.EqualTo(new Rect(200, 200, 200, 400)));
        }

        [Test]
        public void PortraitPhotoPadsLeftAndRight()
        {
            var layout = YoloLetterboxLayout.Create(new Vector2Int(480, 640), new Vector2Int(640, 640));
            Assert.That(layout.resizedSize, Is.EqualTo(new Vector2Int(480, 640)));
            Assert.That(layout.topLeftPadding, Is.EqualTo(new Vector2Int(80, 0)));
            Assert.That(layout.ToSourceRect(new Rect(90, 20, 100, 200)), Is.EqualTo(new Rect(10, 20, 100, 200)));
        }

        [Test]
        public void OddPaddingKeepsItsExtraPixelAtTheBottom()
        {
            var layout = YoloLetterboxLayout.Create(new Vector2Int(640, 461), new Vector2Int(640, 640));
            Assert.That(layout.topLeftPadding, Is.EqualTo(new Vector2Int(0, 89)));
            Assert.That(layout.ContentUvRect.y, Is.EqualTo(90f / 640f));
            Assert.That(layout.ToSourceRect(new Rect(0, 89, 640, 461)), Is.EqualTo(new Rect(0, 0, 640, 461)));
        }

        [Test]
        public void OddPaddingKeepsItsExtraPixelAtTheRight()
        {
            var layout = YoloLetterboxLayout.Create(new Vector2Int(461, 640), new Vector2Int(640, 640));
            Assert.That(layout.topLeftPadding, Is.EqualTo(new Vector2Int(89, 0)));
            Assert.That(layout.ToSourceRect(new Rect(89, 0, 461, 640)), Is.EqualTo(new Rect(0, 0, 461, 640)));
        }

        [Test]
        public void SquareInputNeedsNoPaddingOrCoordinateChange()
        {
            var layout = YoloLetterboxLayout.Create(new Vector2Int(640, 640), new Vector2Int(640, 640));
            var box = new Rect(50, 100, 300, 200);
            Assert.That(layout.topLeftPadding, Is.EqualTo(Vector2Int.zero));
            Assert.That(layout.ToSourceRect(box), Is.EqualTo(box));
        }

        [Test]
        public void PartiallyOffscreenDetectionIsNotSilentlyClamped()
        {
            var layout = YoloLetterboxLayout.Create(new Vector2Int(1280, 960), new Vector2Int(640, 640));
            Assert.That(layout.ToSourceRect(new Rect(-10, 70, 100, 200)), Is.EqualTo(new Rect(-20, -20, 200, 400)));
        }

        [TestCase(0, 480)]
        [TestCase(640, 0)]
        [TestCase(-1, 480)]
        public void InvalidImageDimensionsAreRejected(int width, int height)
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                YoloLetterboxLayout.Create(new Vector2Int(width, height), new Vector2Int(640, 640)));
        }
    }
}
