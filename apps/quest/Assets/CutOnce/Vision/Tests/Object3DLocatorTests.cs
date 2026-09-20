using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class Object3DLocatorTests
    {
        [Test]
        public void PartialObjectsOnlyGenerateRaysThroughVisiblePixels()
        {
            Assert.That(Object3DLocator.TryGetVisibleBox(new Rect(-20f, -10f, 100f, 80f),
                new Vector2(640f, 480f), out var clipped), Is.True);
            Assert.That(clipped, Is.EqualTo(new Rect(0f, 0f, 80f, 70f)));
            Assert.That(Object3DLocator.TryGetVisibleBox(new Rect(600f, 450f, 100f, 80f),
                new Vector2(640f, 480f), out clipped), Is.True);
            Assert.That(clipped, Is.EqualTo(new Rect(600f, 450f, 40f, 30f)));
        }

        [Test]
        public void InvalidOrCompletelyOffscreenBoxesCannotGenerateDepthRays()
        {
            var image = new Vector2(640f, 480f);
            Assert.That(Object3DLocator.TryGetVisibleBox(new Rect(-100f, 0f, 50f, 50f), image, out _), Is.False);
            Assert.That(Object3DLocator.TryGetVisibleBox(new Rect(640f, 0f, 50f, 50f), image, out _), Is.False);
            Assert.That(Object3DLocator.TryGetVisibleBox(new Rect(0f, 0f, 0f, 50f), image, out _), Is.False);
            Assert.That(Object3DLocator.TryGetVisibleBox(new Rect(float.NaN, 0f, 50f, 50f), image, out _), Is.False);
            Assert.That(Object3DLocator.TryGetVisibleBox(new Rect(0f, 0f, 50f, 50f),
                new Vector2(float.PositiveInfinity, 480f), out _), Is.False);
        }

        [Test]
        public void MissingCameraCannotBecomeAGuessedWorldPosition()
        {
            var go = new GameObject("No guessed object positions");
            try
            {
                var locator = go.AddComponent<Object3DLocator>();
                var detection = new DetectedObject
                {
                    className = "bottle", boundingBox = new Rect(100, 100, 50, 150),
                    inputSize = new Vector2(640, 480),
                };
                Assert.That(locator.TryLocate(detection, new Pose(Vector3.zero, Quaternion.identity), out var world, out var size), Is.False);
                Assert.That(world, Is.EqualTo(Vector3.zero));
                Assert.That(size, Is.EqualTo(Vector3.zero), "Failed location must not expose guessed dimensions.");
                Assert.That(locator.LastSuccesses, Is.Zero);
                Assert.That(locator.LastFailureReason, Is.Not.Empty);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void LocatorRejectsInvalidCapturedPoseBeforeConsultingCameraOrDepth()
        {
            var go = new GameObject("Invalid measured geometry");
            try
            {
                var locator = go.AddComponent<Object3DLocator>();
                var detection = new DetectedObject
                {
                    className = "bottle", boundingBox = new Rect(100f, 100f, 50f, 150f),
                    inputSize = new Vector2(640f, 480f),
                };
                var pose = new Pose(new Vector3(float.NaN, 0f, 0f), Quaternion.identity);
                Assert.That(locator.TryLocate(detection, pose, out var world, out var size), Is.False);
                Assert.That(locator.LastFailureReason, Is.EqualTo("invalid captured camera pose"));
                Assert.That(locator.LastSuccesses, Is.Zero);
                Assert.That(world, Is.EqualTo(Vector3.zero));
                Assert.That(size, Is.EqualTo(Vector3.zero));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void OffAxisEdgesMeetOneDepthPlaneAndPreserveMeasuredWidth()
        {
            var pose = new Pose(new Vector3(4f, 1.5f, -2f), Quaternion.Euler(20f, 70f, 12f));
            var leftRay = new Ray(pose.position, pose.rotation * new Vector3(0.4f, 0.2f, 1f));
            var rightRay = new Ray(pose.position, pose.rotation * new Vector3(0.8f, 0.2f, 1f));
            Assert.That(Object3DLocator.TryPointAtForwardDepth(leftRay, pose, 2f, out var left), Is.True);
            Assert.That(Object3DLocator.TryPointAtForwardDepth(rightRay, pose, 2f, out var right), Is.True);

            var forward = pose.rotation * Vector3.forward;
            Assert.That(Vector3.Dot(left - pose.position, forward), Is.EqualTo(2f).Within(0.0001f));
            Assert.That(Vector3.Dot(right - pose.position, forward), Is.EqualTo(2f).Within(0.0001f));
            Assert.That(Vector3.Distance(left, right), Is.EqualTo(0.8f).Within(0.0001f));
        }

        [Test]
        public void DepthPlaneProjectionAccountsForAnOffsetRayOrigin()
        {
            var pose = new Pose(Vector3.zero, Quaternion.identity);
            var ray = new Ray(new Vector3(0.1f, 0f, 0.4f), new Vector3(0.3f, 0f, 1f));
            Assert.That(Object3DLocator.TryPointAtForwardDepth(ray, pose, 2f, out var point), Is.True);
            Assert.That(point.z, Is.EqualTo(2f).Within(0.0001f));
            Assert.That(point.x, Is.EqualTo(0.58f).Within(0.0001f));
        }

        [Test]
        public void InvalidDepthAndParallelOrBackwardRaysAreRejected()
        {
            var pose = new Pose(Vector3.zero, Quaternion.identity);
            foreach (var depth in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                Assert.That(Object3DLocator.TryPointAtForwardDepth(new Ray(Vector3.zero, Vector3.forward), pose, depth, out _), Is.False);
            foreach (var direction in new[] { Vector3.right, Vector3.back })
                Assert.That(Object3DLocator.TryPointAtForwardDepth(new Ray(Vector3.zero, direction), pose, 2f, out _), Is.False);
            Assert.That(Object3DLocator.TryPointAtForwardDepth(new Ray(Vector3.forward * 3f, Vector3.forward), pose, 2f, out _), Is.False);
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void NonfiniteMeasuredHitComponentsCannotEnterDepthSamples(float invalid)
        {
            var pose = new Pose(Vector3.zero, Quaternion.identity);
            for (var axis = 0; axis < 3; axis++)
            {
                var hit = new Vector3(0f, 0f, 2f);
                hit[axis] = invalid;
                Assert.That(Object3DLocator.TryGetMeasuredDepth(hit, pose, .2f, 6f, out var depth), Is.False);
                Assert.That(depth, Is.Zero, "A failed measurement must not expose NaN or infinity.");
            }
        }

        [TestCase(-1f, false)]
        [TestCase(0f, false)]
        [TestCase(.19f, false)]
        [TestCase(.2f, true)]
        [TestCase(2f, true)]
        [TestCase(6f, true)]
        [TestCase(6.01f, false)]
        public void OnlyMeasuredForwardDepthInsideTheConfiguredIntervalIsAdmitted(float z, bool expected)
        {
            // Nonzero lateral components must not turn a valid forward depth into a ray distance.
            var hit = new Vector3(3f, -1f, z);
            Assert.That(Object3DLocator.TryGetMeasuredDepth(hit, new Pose(Vector3.zero, Quaternion.identity),
                .2f, 6f, out var depth), Is.EqualTo(expected));
            Assert.That(depth, Is.EqualTo(expected ? z : 0f));
        }

        [Test]
        public void InvalidDepthIntervalsAndOverflowedMeasurementsFailClosed()
        {
            var pose = new Pose(Vector3.zero, Quaternion.identity);
            foreach (var interval in new[]
                     { new Vector2(float.NaN, 6f), new Vector2(.2f, float.PositiveInfinity),
                         new Vector2(0f, 6f), new Vector2(6f, .2f) })
                Assert.That(Object3DLocator.TryGetMeasuredDepth(Vector3.forward * 2f, pose,
                    interval.x, interval.y, out _), Is.False);
            pose.position = new Vector3(0f, 0f, -float.MaxValue);
            Assert.That(Object3DLocator.TryGetMeasuredDepth(new Vector3(0f, 0f, float.MaxValue),
                pose, .2f, 6f, out _), Is.False);
        }

        [Test]
        public void InvalidPoseOrRayCannotProduceASuccessfulWorldPoint()
        {
            var validPose = new Pose(Vector3.zero, Quaternion.identity);
            var validRay = new Ray(Vector3.zero, Vector3.forward);
            foreach (var pose in new[]
                     {
                         new Pose(new Vector3(float.NaN, 0f, 0f), Quaternion.identity),
                         new Pose(new Vector3(0f, float.PositiveInfinity, 0f), Quaternion.identity),
                         new Pose(Vector3.zero, new Quaternion(0f, 0f, 0f, 0f)),
                         new Pose(Vector3.zero, new Quaternion(float.NaN, 0f, 0f, 1f)),
                         new Pose(Vector3.zero, new Quaternion(0f, float.PositiveInfinity, 0f, 1f)),
                         new Pose(Vector3.zero, new Quaternion(float.MaxValue, 0f, 0f, 1f)),
                     })
            {
                Assert.That(Object3DLocator.TryPointAtForwardDepth(validRay, pose, 2f, out var point), Is.False);
                Assert.That(point, Is.EqualTo(Vector3.zero));
                Assert.That(Object3DLocator.TryGetMeasuredDepth(Vector3.forward * 2f, pose, .2f, 6f, out _), Is.False);
            }
            foreach (var ray in new[]
                     {
                         new Ray(new Vector3(float.NaN, 0f, 0f), Vector3.forward),
                         new Ray(new Vector3(0f, float.PositiveInfinity, 0f), Vector3.forward),
                         new Ray(Vector3.zero, new Vector3(float.NaN, 0f, 1f)),
                         new Ray(Vector3.zero, Vector3.zero),
                     })
            {
                Assert.That(Object3DLocator.TryPointAtForwardDepth(ray, validPose, 2f, out var point), Is.False);
                Assert.That(point, Is.EqualTo(Vector3.zero));
            }
        }

        [Test]
        public void FiniteInputsThatOverflowTheProjectedWorldPointAreRejected()
        {
            var origin = new Vector3(float.MaxValue, 0f, 0f);
            var pose = new Pose(origin, Quaternion.identity);
            var ray = new Ray(origin, new Vector3(1f, 0f, .2f));
            Assert.That(Object3DLocator.TryPointAtForwardDepth(ray, pose, 5e37f, out var point), Is.False);
            Assert.That(point, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void SideViewRotatesWidthIntoWorldDepth()
        {
            var actual = Object3DLocator.WorldAlignedSize(new Vector3(2f, 0.8f, 0.3f), Quaternion.Euler(0f, 90f, 0f));
            Assert.That(actual.x, Is.EqualTo(0.3f).Within(0.0001f));
            Assert.That(actual.y, Is.EqualTo(0.8f).Within(0.0001f));
            Assert.That(actual.z, Is.EqualTo(2f).Within(0.0001f));
        }

        [TestCase(15f, 35f, 20f)]
        [TestCase(-55f, 110f, -30f)]
        public void TiltedWorldBoundsContainEveryRotatedCorner(float pitch, float yaw, float roll)
        {
            var size = new Vector3(2.4f, 0.8f, 1.2f);
            var rotation = Quaternion.Euler(pitch, yaw, roll);
            var extent = Object3DLocator.WorldAlignedSize(size, rotation) * 0.5f;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var corner = rotation * Vector3.Scale(size * 0.5f, new Vector3(x, y, z));
                for (var axis = 0; axis < 3; axis++)
                    Assert.That(Mathf.Abs(corner[axis]), Is.LessThanOrEqualTo(extent[axis] + 0.0001f));
            }
        }

        [TestCase("dining table")]
        [TestCase("diningtable")]
        [TestCase("table")]
        public void TablesKeepFullWidthAndFootprintEvenWhenSeenEdgeOn(string label)
        {
            var size = Object3DLocator.EstimateCameraSize(2.4f, 0.2f, label, 0.05f, 1.2f);
            Assert.That(size.x, Is.EqualTo(2.4f));
            Assert.That(size.y, Is.EqualTo(0.2f));
            Assert.That(size.z, Is.EqualTo(1.5f));
            var capped = Object3DLocator.EstimateCameraSize(20f, 20f, label, 0.05f, 1.2f);
            Assert.That(capped, Is.EqualTo(new Vector3(3f, 3f, 1.5f)));
        }

        [Test]
        public void PersonHeightAndOrdinaryObjectCapsRemainBounded()
        {
            var person = Object3DLocator.EstimateCameraSize(0.7f, 2.8f, "person", 0.05f, 1.2f);
            Assert.That(person, Is.EqualTo(new Vector3(0.7f, 2f, 0.6f)));
            var bottle = Object3DLocator.EstimateCameraSize(5f, 5f, "bottle", 0.05f, 1.2f);
            Assert.That(bottle, Is.EqualTo(new Vector3(1.2f, 1.2f, 0.6f)));
            var tiny = Object3DLocator.EstimateCameraSize(0f, 0f, "bottle", 0.05f, 1.2f);
            Assert.That(tiny, Is.EqualTo(Vector3.one * 0.05f));
        }
    }
}
