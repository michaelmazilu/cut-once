using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class CapturedCameraProjectionTests
    {
        private static readonly Pose Identity = new Pose(Vector3.zero, Quaternion.identity);

        private static CapturedCameraProjection Wide(Pose pose)
        {
            Assert.That(CapturedCameraProjection.TryCreate(pose, new Vector2(1000f, 900f),
                new Vector2(800f, 600f), new Vector2Int(1600, 1200), new Vector2Int(1280, 720),
                out var projection), Is.True);
            return projection;
        }

        private static void Uv(CapturedCameraProjection projection, Vector3 world, float u, float v)
        {
            Assert.That(projection.TryProject(world, out var actual), Is.True);
            Assert.That(actual.x, Is.EqualTo(u).Within(0.00001f));
            Assert.That(actual.y, Is.EqualTo(v).Within(0.00001f));
        }

        [Test]
        public void OffAxisPrincipalPointMatchesKnownSensorPixelsAndFlipsYOnce()
        {
            Assert.That(CapturedCameraProjection.TryCreate(Identity, new Vector2(800f, 600f),
                new Vector2(600f, 300f), new Vector2Int(1000, 800), new Vector2Int(500, 400),
                out var projection), Is.True);
            Assert.That(projection.SensorCrop, Is.EqualTo(new Rect(0f, 0f, 1000f, 800f)));
            // Camera (.25,.5,2) projects to sensor (700,450); top-left image UV=(.7,.4375).
            Uv(projection, new Vector3(.25f, .5f, 2f), .7f, .4375f);
            Uv(projection, Vector3.forward * 2f, .6f, .625f);
        }

        [Test]
        public void WideStreamRemovesSensorTopAndBottomBeforeNormalizing()
        {
            var projection = Wide(Identity);
            Assert.That(projection.SensorCrop, Is.EqualTo(new Rect(0f, 150f, 1600f, 900f)));
            Assert.That(projection.ImageResolution, Is.EqualTo(new Vector2Int(1280, 720)));
            Uv(projection, new Vector3(.4f, .2f, 2f), .625f, .4f);
        }

        [Test]
        public void PortraitStreamRemovesSensorSidesBeforeNormalizing()
        {
            Assert.That(CapturedCameraProjection.TryCreate(Identity, new Vector2(1000f, 900f),
                new Vector2(800f, 600f), new Vector2Int(1600, 1200), new Vector2Int(600, 800),
                out var projection), Is.True);
            Assert.That(projection.SensorCrop, Is.EqualTo(new Rect(350f, 0f, 900f, 1200f)));
            Uv(projection, new Vector3(.18f, -.4f, 2f), .6f, .65f);
        }

        [Test]
        public void ProjectionUsesCapturedLensPoseIncludingTranslationAndRotation()
        {
            var capturedLens = new Pose(new Vector3(4f, 1.5f, -3f), Quaternion.Euler(15f, 75f, -8f));
            var projection = Wide(capturedLens);
            var world = capturedLens.position + capturedLens.rotation * new Vector3(.4f, .2f, 2f);
            Uv(projection, world, .625f, .4f);
            var homogeneous = projection.WorldToMask * new Vector4(world.x, world.y, world.z, 1f);
            Assert.That(homogeneous.w, Is.EqualTo(2f).Within(0.00001f));
            Assert.That(homogeneous.z, Is.EqualTo(homogeneous.w));
            Assert.That(homogeneous.x / homogeneous.w, Is.EqualTo(.625f).Within(0.00001f));
            Assert.That(homogeneous.y / homogeneous.w, Is.EqualTo(.4f).Within(0.00001f));
        }

        [Test]
        public void BothRenderingEyesProjectTheSameWorldSurfaceIntoTheCapturedRgbMask()
        {
            var projection = Wide(Identity);
            var surface = new Vector3(.4f, .2f, 2f);
            // The rendering head has translated since capture. Neither eye becomes the RGB lens.
            var leftEye = new Vector3(.168f, .1f, .2f);
            var rightEye = new Vector3(.232f, .1f, .2f);
            var left = new Ray(leftEye, surface - leftEye);
            var right = new Ray(rightEye, surface - rightEye);
            Assert.That(Vector3.Distance(left.direction, right.direction), Is.GreaterThan(.01f));
            Uv(projection, left.GetPoint(Vector3.Distance(leftEye, surface)), .625f, .4f);
            Uv(projection, right.GetPoint(Vector3.Distance(rightEye, surface)), .625f, .4f);
            Assert.That(projection.CameraPose.position, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void KnownSdkViewportRaysPreservePerspectiveRatherThanInterpolatingUnitDirections()
        {
            Assert.That(CapturedCameraProjection.TryCreate(Identity, new Vector2(800f, 800f),
                new Vector2(640f, 480f), new Vector2Int(1280, 960), new Vector2Int(1280, 960),
                out var projection), Is.True);
            // Numeric SDK viewport-to-ray results: (.75,.25) -> (.4,-.3,1),
            // and (.25,.75) -> (-.4,.3,1), before Ray normalizes the direction.
            var lowerRight = new Ray(Vector3.zero, new Vector3(.4f, -.3f, 1f));
            var upperLeft = new Ray(Vector3.zero, new Vector3(-.4f, .3f, 1f));
            Uv(projection, lowerRight.GetPoint(1f), .75f, .75f);
            Uv(projection, lowerRight.GetPoint(4f), .75f, .75f);
            Uv(projection, upperLeft.GetPoint(2f), .25f, .25f);
        }

        [Test]
        public void BoundaryCoordinatesAreNotConfusedWithOutsidePixels()
        {
            var projection = Wide(Identity);
            Uv(projection, new Vector3(-1.6f, 1f, 2f), 0f, 0f);
            Uv(projection, new Vector3(1.6f, -1f, 2f), 1f, 1f);
            foreach (var point in new[] { new Vector3(-1.61f, 0f, 2f), new Vector3(1.61f, 0f, 2f),
                new Vector3(0f, 1.01f, 2f), new Vector3(0f, -1.01f, 2f) })
                Assert.That(projection.TryProject(point, out _), Is.False);
        }

        [Test]
        public void BehindCameraDegenerateAndNonfiniteWorldPointsAreRejected()
        {
            var projection = Wide(Identity);
            foreach (var point in new[] { Vector3.zero, Vector3.back, new Vector3(1f, 1f, 0f),
                new Vector3(float.NaN, 0f, 1f), new Vector3(0f, float.PositiveInfinity, 1f),
                new Vector3(0f, 0f, float.NegativeInfinity) })
            {
                Assert.That(projection.TryProject(point, out var uv), Is.False);
                Assert.That(uv, Is.EqualTo(Vector2.zero));
            }
            Assert.That(default(CapturedCameraProjection).TryProject(Vector3.forward, out _), Is.False);
        }

        [Test]
        public void InvalidCalibrationCannotProduceAUsableProjection()
        {
            var size = new Vector2Int(1280, 960);
            var focal = new Vector2(800f, 800f);
            var centre = new Vector2(640f, 480f);
            foreach (var invalid in new[] { Vector2.zero, new Vector2(-1f, 800f), new Vector2(float.NaN, 800f),
                new Vector2(800f, float.PositiveInfinity) })
                Assert.That(CapturedCameraProjection.TryCreate(Identity, invalid, centre, size, size, out _), Is.False);
            foreach (var invalid in new[] { Vector2Int.zero, new Vector2Int(-1, 960), new Vector2Int(1280, 0) })
            {
                Assert.That(CapturedCameraProjection.TryCreate(Identity, focal, centre, invalid, size, out _), Is.False);
                Assert.That(CapturedCameraProjection.TryCreate(Identity, focal, centre, size, invalid, out _), Is.False);
            }
            Assert.That(CapturedCameraProjection.TryCreate(Identity, focal, new Vector2(float.NaN, 0f), size, size, out _), Is.False);
            foreach (var invalidPose in new[] { new Pose(Vector3.zero, new Quaternion(0f, 0f, 0f, 0f)),
                new Pose(new Vector3(float.NaN, 0f, 0f), Quaternion.identity),
                new Pose(Vector3.zero, new Quaternion(float.PositiveInfinity, 0f, 0f, 1f)) })
            {
                Assert.That(CapturedCameraProjection.TryCreate(invalidPose, focal, centre, size, size, out var invalid), Is.False);
                Assert.That(invalid.IsValid, Is.False);
            }
        }
    }
}
