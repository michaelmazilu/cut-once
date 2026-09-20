using System;
using System.Collections.Generic;

namespace CutOnce.Core.Vision
{
    /// <summary>
    /// A point in metres in the room's frame (+Y up). Core carries no UnityEngine, so this is its vector; the headset
    /// converts to and from UnityEngine.Vector3 at the edge.
    /// </summary>
    public readonly struct P3
    {
        public readonly float X, Y, Z;
        public P3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static P3 operator +(P3 a, P3 b) => new P3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static P3 operator -(P3 a, P3 b) => new P3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static P3 operator *(P3 a, float k) => new P3(a.X * k, a.Y * k, a.Z * k);

        public float Length => (float)Math.Sqrt((double)X * X + (double)Y * Y + (double)Z * Z);
        public bool IsFinite =>
            !float.IsNaN(X) && !float.IsNaN(Y) && !float.IsNaN(Z) &&
            !float.IsInfinity(X) && !float.IsInfinity(Y) && !float.IsInfinity(Z);

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";
    }

    /// <summary>
    /// The measurements a box fit needs. Everything here is robust by construction: percentiles rather than min/max,
    /// and a hull-based yaw rather than an assumption that objects face the room's axes.
    /// </summary>
    public static class BoxMath
    {
        /// <summary>The value at a fraction of the way through a SORTED list. Linear between neighbours.</summary>
        public static float Percentile(IReadOnlyList<float> sorted, float fraction)
        {
            if (sorted == null || sorted.Count == 0) return 0f;
            if (sorted.Count == 1) return sorted[0];
            var t = Clamp01(fraction) * (sorted.Count - 1);
            var i = (int)Math.Floor(t);
            var j = Math.Min(i + 1, sorted.Count - 1);
            return sorted[i] + (sorted[j] - sorted[i]) * (t - i);
        }

        public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

        /// <summary>
        /// The convex hull of the points' footprint (X, Z), counter-clockwise, by Andrew's monotone chain. The hull is
        /// what the yaw fit walks: a few dozen points at most, whatever the cloud's size.
        /// </summary>
        public static List<P3> HullXz(List<P3> points)
        {
            var result = new List<P3>();
            if (points == null || points.Count == 0) return result;
            var sorted = new List<P3>(points);
            sorted.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z));
            var n = sorted.Count;
            if (n < 3)
            {
                result.AddRange(sorted);
                return result;
            }

            var hull = new P3[2 * n];
            var k = 0;
            for (var i = 0; i < n; i++)                        // lower half, left to right
            {
                while (k >= 2 && Cross(hull[k - 2], hull[k - 1], sorted[i]) <= 0f) k--;
                hull[k++] = sorted[i];
            }
            var lower = k + 1;
            for (var i = n - 2; i >= 0; i--)                   // upper half, back again
            {
                while (k >= lower && Cross(hull[k - 2], hull[k - 1], sorted[i]) <= 0f) k--;
                hull[k++] = sorted[i];
            }
            for (var i = 0; i < k - 1; i++) result.Add(hull[i]);   // the last point is the first one again
            return result;
        }

        static float Cross(P3 o, P3 a, P3 b) => (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);

        /// <summary>
        /// The yaw of the smallest rectangle that encloses the footprint, in degrees, in UNITY'S convention: the angle
        /// you would pass to Quaternion.Euler(0, yaw, 0) to stand a box on this footprint. A turn about +Y takes +X to
        /// (cos, 0, -sin), so this is the NEGATIVE of the angle atan2 reads off the same edge — mixing the two mirrors
        /// every box, which looks almost right until the object is not square.
        ///
        /// Rotating calipers: the minimum rectangle always shares an edge with the hull, so every hull edge is tried
        /// and the tightest one wins. Gravity-constrained on purpose — a box on a table is turned, never tipped, and a
        /// free 3-axis fit on a single-view cloud tilts drunkenly.
        /// </summary>
        public static float MinAreaYawDeg(List<P3> hull) => MinAreaYawDeg(hull, null, 0f);

        /// <summary>
        /// The same fit, with the tie broken by the cloud. From one viewpoint a box shows two faces, so its footprint
        /// is an L and its hull a right triangle — and a right triangle's smallest rectangle is EXACTLY TIED between
        /// its legs and its hypotenuse, both of area ab. Rounding then picks one, and half the time that is the
        /// hypotenuse: a bag sitting square to the room drawn 34° askew.
        ///
        /// The tie is broken by asking which edge the depth actually lies along. A hypotenuse is a chord over thin
        /// air; the faces have points all the way down them. `bandM` is how near a point must be to count, and should
        /// be about the spacing of the samples.
        /// </summary>
        public static float MinAreaYawDeg(List<P3> hull, IReadOnlyList<P3> points, float bandM)
        {
            if (hull == null || hull.Count < 3) return 0f;

            var bestArea = float.MaxValue;
            for (var i = 0; i < hull.Count; i++)
            {
                if (!EdgeDirection(hull, i, out var ex, out var ez)) continue;
                var area = SpanArea(hull, ex, ez);
                if (area < bestArea) bestArea = area;
            }
            if (bestArea == float.MaxValue) return 0f;

            // Every edge within a whisker of the best area is a real candidate, not a runner-up.
            var limit = bestArea * (1f + AreaTie);
            var bestYaw = 0f;
            var bestSupport = -1;
            for (var i = 0; i < hull.Count; i++)
            {
                if (!EdgeDirection(hull, i, out var ex, out var ez)) continue;
                if (SpanArea(hull, ex, ez) > limit) continue;
                var support = points == null ? 0 : PointsAlong(points, hull[i], ex, ez, bandM);
                if (support <= bestSupport) continue;
                bestSupport = support;
                bestYaw = (float)(-Math.Atan2(ez, ex) * 180.0 / Math.PI);   // atan2 turns the other way to Unity
            }
            return Wrap90(bestYaw);
        }

        /// <summary>How much worse than the best an edge's rectangle may be and still count as tied.</summary>
        const float AreaTie = 0.05f;

        static bool EdgeDirection(List<P3> hull, int i, out float ex, out float ez)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            ex = b.X - a.X;
            ez = b.Z - a.Z;
            var len = (float)Math.Sqrt((double)ex * ex + (double)ez * ez);
            if (len < 1e-5f) { ex = ez = 0f; return false; }
            ex /= len; ez /= len;
            return true;
        }

        static float SpanArea(List<P3> hull, float ex, float ez)
        {
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            foreach (var p in hull)
            {
                var u = p.X * ex + p.Z * ez;          // along the edge
                var v = -p.X * ez + p.Z * ex;         // across it
                if (u < uMin) uMin = u;
                if (u > uMax) uMax = u;
                if (v < vMin) vMin = v;
                if (v > vMax) vMax = v;
            }
            return (uMax - uMin) * (vMax - vMin);
        }

        /// <summary>How many points lie within `band` of the line through `from` along the edge.</summary>
        static int PointsAlong(IReadOnlyList<P3> points, P3 from, float ex, float ez, float band)
        {
            if (band <= 0f) return 0;
            var count = 0;
            for (var i = 0; i < points.Count; i++)
            {
                var dx = points[i].X - from.X;
                var dz = points[i].Z - from.Z;
                var across = dx * -ez + dz * ex;
                if (across < 0f) across = -across;
                if (across <= band) count++;
            }
            return count;
        }

        /// <summary>World to the box's own frame: the inverse of a turn by `yawDeg` about +Y.</summary>
        public static void ToBoxFrame(P3 p, float cos, float sin, out float u, out float v)
        {
            u = p.X * cos - p.Z * sin;
            v = p.X * sin + p.Z * cos;
        }

        /// <summary>The box's own frame back to the world: a turn by `yawDeg` about +Y.</summary>
        public static P3 FromBoxFrame(float u, float y, float v, float cos, float sin) =>
            new P3(u * cos + v * sin, y, -u * sin + v * cos);

        /// <summary>A box turned 90° is the same box: yaw is reported in (-45, 45] so smoothing never chases a flip.</summary>
        public static float Wrap90(float deg)
        {
            while (deg > 45f) deg -= 90f;
            while (deg <= -45f) deg += 90f;
            return deg;
        }

        /// <summary>The shortest signed turn from `from` to `to`, both in the (-45, 45] convention.</summary>
        public static float DeltaYaw(float from, float to) => Wrap90(to - from);
    }
}
