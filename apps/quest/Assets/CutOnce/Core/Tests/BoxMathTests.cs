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
        public void TheBoxFrameAndTheWorldAreExactInverses()
        {
            // DepthScene turns its bodies with the same two helpers the fit uses to read a turn back, which makes
            // every scene-driven yaw assertion self-consistent even if BOTH were mirrored. This pins them to
            // hand-computed numbers instead: at 90°, Unity takes +X to (0, 0, -1), so a point on +X reads back as
            // v = +1 in the box's frame.
            var r = 90f * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(r), sin = (float)Math.Sin(r);
            BoxMath.ToBoxFrame(new P3(1f, 0f, 0f), cos, sin, out var u, out var v);
            Assert.That(u, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(v, Is.EqualTo(1f).Within(1e-5f));

            var back = BoxMath.FromBoxFrame(0f, 0f, 1f, cos, sin);
            Assert.That(back.X, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(back.Z, Is.EqualTo(0f).Within(1e-5f));

            // …and they invert each other at an awkward angle, not just at right angles.
            var odd = 37f * (float)Math.PI / 180f;
            float c2 = (float)Math.Cos(odd), s2 = (float)Math.Sin(odd);
            var start = new P3(0.31f, 0.8f, -0.17f);
            BoxMath.ToBoxFrame(start, c2, s2, out var u2, out var v2);
            var round = BoxMath.FromBoxFrame(u2, start.Y, v2, c2, s2);
            Assert.That(round.X, Is.EqualTo(start.X).Within(1e-5f));
            Assert.That(round.Z, Is.EqualTo(start.Z).Within(1e-5f));
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
