using System;
using System.Collections.Generic;
using CutOnce.Core.Vision;
using NUnit.Framework;

namespace CutOnce.Core.Tests
{
    /// <summary>The measurements under the fit: the footprint's hull, the turn it implies, and the percentile reads.</summary>
    public class BoxMathTests
    {
        /// <summary>A flat grid of points, turned about +Y by `yawDeg` the way Unity turns things.</summary>
        static List<P3> Slab(float alongX, float alongZ, float yawDeg, int steps = 12, float y = 0.8f)
        {
            var r = yawDeg * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(r), sin = (float)Math.Sin(r);
            var points = new List<P3>();
            for (var i = 0; i < steps; i++)
                for (var j = 0; j < steps; j++)
                {
                    var u = (i / (float)(steps - 1) - 0.5f) * alongX;
                    var v = (j / (float)(steps - 1) - 0.5f) * alongZ;
                    points.Add(BoxMath.FromBoxFrame(u, y, v, cos, sin));
                }
            return points;
        }

        static List<P3> Square(float side, float yawDeg, int steps = 12, float y = 0.8f) =>
            Slab(side, side, yawDeg, steps, y);

        [Test]
        public void TheHullOfASquareIsItsFourCorners()
        {
            var hull = BoxMath.HullXz(Square(0.4f, 0f));
            Assert.That(hull.Count, Is.EqualTo(4), "an axis-aligned square has four hull points once collinear ones are dropped");
        }

        [Test]
        public void ReadsTheTurnOfASquare()
        {
            foreach (var yaw in new[] { 0f, 12f, 30f, 44f, -25f })
            {
                var measured = BoxMath.MinAreaYawDeg(BoxMath.HullXz(Square(0.35f, yaw)));
                Assert.That(Math.Abs(BoxMath.DeltaYaw(yaw, measured)), Is.LessThan(4f), $"turned {yaw}°, read {measured:0.#}°");
            }
        }

        [Test]
        public void TheTurnOfALongThingFollowsItsLongSide()
        {
            // A 40 x 6 cm slab turned 20°. Unlike a square, this one only reads correctly under ONE sign convention:
            // get it backwards and the box is mirrored across the room's axis.
            var yaw = BoxMath.MinAreaYawDeg(BoxMath.HullXz(Slab(0.4f, 0.06f, 20f, 24)));
            Assert.That(Math.Abs(BoxMath.DeltaYaw(20f, yaw)), Is.LessThan(4f), $"read {yaw:0.#}°");
        }

        [Test]
        public void TheYawIsTheOneUnityWouldUse()
        {
            // Turning a slab by +25° about +Y takes its long axis towards -Z, because that is what
            // Quaternion.Euler(0, 25, 0) does. If the fit ever reports atan2's angle instead, this catches it.
            var turned = Slab(0.4f, 0.06f, 25f, 24);
            float far = float.MinValue, farZ = 0f;
            foreach (var p in turned)
                if (p.X > far) { far = p.X; farZ = p.Z; }
            Assert.That(farZ, Is.LessThan(0f), "a +25° turn must push the +X end towards -Z, as Unity does");
            Assert.That(BoxMath.MinAreaYawDeg(BoxMath.HullXz(turned)), Is.EqualTo(25f).Within(4f));
        }

        [Test]
        public void AQuarterTurnIsTheSameBox()
        {
            Assert.That(BoxMath.Wrap90(90f), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(BoxMath.Wrap90(46f), Is.EqualTo(-44f).Within(1e-4f));
            Assert.That(Math.Abs(BoxMath.DeltaYaw(44f, -44f)), Is.LessThan(3f), "44° and -44° are two degrees apart, not 88");
        }

        [Test]
        public void PercentilesIgnoreTheStrays()
        {
            var values = new List<float>();
            for (var i = 0; i < 100; i++) values.Add(1f + i * 0.001f);   // 1.000 … 1.099
            values.Add(9f);                                              // one wild depth sample
            values.Sort();
            Assert.That(BoxMath.Percentile(values, 0.98f), Is.LessThan(1.2f), "a single stray must not stretch the read");
        }
    }
}
