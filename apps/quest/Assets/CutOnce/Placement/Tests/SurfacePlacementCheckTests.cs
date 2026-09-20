using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Placement.Tests
{
    public sealed class SurfacePlacementCheckTests
    {
        static readonly Bounds Box = new Bounds(Vector3.zero, Vector3.one * .4f);
        static readonly Vector3 Eye = new Vector3(1, 1, -1);

        sealed class MeasuredBox : IPlacementSurfaceSource
        {
            public Bounds bounds = Box;
            public Matrix4x4 pose = Matrix4x4.identity;
            public float confidence = 1;
            public bool missing;
            public bool Raycast(Ray ray, out Vector3 point, out Vector3 normal, out float quality)
            {
                var inverse = pose.inverse;
                var local = new Ray(inverse.MultiplyPoint3x4(ray.origin), inverse.MultiplyVector(ray.direction));
                point = normal = default; quality = confidence;
                if (missing || !bounds.IntersectRay(local, out float distance)) return false;
                var p = local.GetPoint(distance);
                var relative = p - bounds.center;
                int axis = 0;
                float error = float.PositiveInfinity;
                for (int i = 0; i < 3; i++)
                {
                    float e = Mathf.Abs(Mathf.Abs(relative[i]) - bounds.extents[i]);
                    if (e < error) { error = e; axis = i; }
                }
                normal[axis] = Mathf.Sign(relative[axis]);
                normal = pose.MultiplyVector(normal); point = pose.MultiplyPoint3x4(p);
                return true;
            }
        }

        sealed class Wall : IPlacementSurfaceSource
        {
            public bool Raycast(Ray ray, out Vector3 point, out Vector3 normal, out float quality)
            {
                normal = Vector3.back; quality = 1;
                point = ray.GetPoint((-.2f - ray.origin.z) / ray.direction.z);
                return true;
            }
        }

        static SurfacePlacementState Sample(SurfacePlacementCheck check, IPlacementSurfaceSource source, double t) =>
            check.Evaluate(source, Eye, Matrix4x4.identity, Box, t, t);

        [Test] public void IndependentBoxMeasurementsMatchAfterFreshDwell()
        {
            var c = new SurfacePlacementCheck(); var s = new MeasuredBox();
            for (int i = 0; i < 5; i++) Assert.That(Sample(c, s, i * .1), Is.EqualTo(SurfacePlacementState.Aligning));
            Assert.That(Sample(c, s, .5), Is.EqualTo(SurfacePlacementState.Matches));
            Assert.That(c.Faces, Is.GreaterThanOrEqualTo(2));
        }

        [Test] public void FrozenImageNeverAdvancesAndExpires()
        {
            var c = new SurfacePlacementCheck(); var s = new MeasuredBox();
            Sample(c, s, 0);
            Assert.That(c.Evaluate(s, Eye, Matrix4x4.identity, Box, 0, .2), Is.EqualTo(SurfacePlacementState.Aligning));
            Assert.That(c.Evaluate(s, Eye, Matrix4x4.identity, Box, 0, .3), Is.EqualTo(SurfacePlacementState.Unavailable));
        }

        [Test] public void AWallCannotConfirmTheTargetEvenWhenItsFrontPlaneMatches()
        {
            var c = new SurfacePlacementCheck();
            for (int i = 0; i < 10; i++) Assert.That(Sample(c, new Wall(), i * .1), Is.Not.EqualTo(SurfacePlacementState.Matches));
        }

        [Test] public void MovedOrRotatedPhysicalBoxDoesNotPass()
        {
            foreach (var pose in new[] { Matrix4x4.Translate(Vector3.right * .15f), Matrix4x4.Rotate(Quaternion.Euler(0, 30, 0)) })
            {
                var c = new SurfacePlacementCheck(); var s = new MeasuredBox { pose = pose };
                for (int i = 0; i < 10; i++) Assert.That(Sample(c, s, i * .1), Is.Not.EqualTo(SurfacePlacementState.Matches));
            }
        }

        [Test] public void LowConfidenceAndMissingDepthClearGreenImmediately()
        {
            var c = new SurfacePlacementCheck(); var s = new MeasuredBox();
            for (int i = 0; i <= 5; i++) Sample(c, s, i * .1);
            s.confidence = .3f;
            Assert.That(Sample(c, s, .6), Is.EqualTo(SurfacePlacementState.Unavailable));
            s.confidence = 1; s.missing = true;
            Assert.That(Sample(c, s, .7), Is.EqualTo(SurfacePlacementState.Unavailable));
            s.missing = false;
            Assert.That(Sample(c, s, .8), Is.EqualTo(SurfacePlacementState.Aligning));
        }

        [Test] public void OneVisibleFaceCannotPass()
        {
            var c = new SurfacePlacementCheck(); var s = new MeasuredBox();
            Assert.That(c.Evaluate(s, Vector3.back, Matrix4x4.identity, Box, 0, 0), Is.EqualTo(SurfacePlacementState.Unavailable));
        }

        [Test] public void ObservationGapsAndNewTargetsRequireNewDwell()
        {
            var c = new SurfacePlacementCheck(); var s = new MeasuredBox();
            for (int i = 0; i <= 5; i++) Sample(c, s, i * .1);
            Assert.That(Sample(c, s, 1), Is.EqualTo(SurfacePlacementState.Aligning));
            for (int i = 11; i <= 16; i++) Sample(c, s, i * .1);
            s.pose = Matrix4x4.Translate(Vector3.right * .01f);
            Assert.That(c.Evaluate(s, Eye, s.pose, Box, 1.7, 1.7), Is.EqualTo(SurfacePlacementState.Aligning));
        }

        [Test] public void FutureAndOutOfOrderFramesDoNotPass()
        {
            var c = new SurfacePlacementCheck(); var s = new MeasuredBox();
            Assert.That(c.Evaluate(s, Eye, Matrix4x4.identity, Box, 2, 1), Is.EqualTo(SurfacePlacementState.Unavailable));
            Sample(c, s, 2);
            Assert.That(Sample(c, s, 1), Is.EqualTo(SurfacePlacementState.Unavailable));
        }
    }
}
