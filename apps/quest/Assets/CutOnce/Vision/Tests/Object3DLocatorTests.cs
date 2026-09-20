using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class Object3DLocatorTests
    {
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
