using System;
using System.Collections.Generic;
using CutOnce.Core.Vision;

namespace CutOnce.Core.Tests
{
    /// <summary>
    /// A simulated depth patch, built the way the headset builds a real one: rays through the pixels of a DETECTION
    /// RECTANGLE, each stopping at the nearest thing it meets — the object, the table it stands on, or the wall behind.
    ///
    /// The rectangle always spills past the object, because a real detector's box does. That spill is the whole
    /// problem the fitter exists to solve, so the simulation must contain it: an object 25 cm tall against a wall 3 m
    /// away is exactly the case that used to produce a metre-wide cyan box.
    /// </summary>
    public static class DepthScene
    {
        public sealed class Body
        {
            public P3 Centre;             // centre of the object, metres, +Y up
            public P3 Size;               // full extents (x, y, z) before the turn
            public float YawDeg;          // turn about up
            public bool Cylinder;         // round in plan (a bottle, a can)
            public bool Invisible;        // clear plastic: the depth sensor sees straight through it
        }

        public sealed class Options
        {
            public float TableY = 0.74f;      // the desk the object stands on; below this is not seen
            public float WallZ = 3.0f;        // the far wall, in front of the camera
            public int Pixels = 24;           // rays across the rectangle (24 x 24 = 576, like a real patch)
            public float Margin = 0.35f;      // how far the detection rectangle spills past the object, as a fraction
            public float NoiseM = 0.004f;     // depth noise: UNIFORM on +/-NoiseM, so no tails. Real depth has them,
                                              // and tails are exactly what the percentile trimming exists to survive,
                                              // so this understates the case the trimming is there for.
            public int Seed = 7;
        }

        /// <summary>
        /// Where the detection rectangle is and which way its rays point. Both pipelines are driven through this, so
        /// a comparison between them is a comparison of the maths, not of two different scenes.
        /// </summary>
        public readonly struct View
        {
            public readonly P3 Camera, Forward, Right, Up;
            public readonly float UCentre, VCentre, Across, Tall;

            public View(P3 camera, P3 forward, P3 right, P3 up, float uCentre, float vCentre, float across, float tall)
            {
                Camera = camera; Forward = forward; Right = right; Up = up;
                UCentre = uCentre; VCentre = vCentre; Across = across; Tall = tall;
            }

            /// <summary>The ray through a point in the rectangle, both 0..1 from one corner to the other.</summary>
            public P3 At(float x, float y) =>
                Normalise(Forward + Right * (UCentre + (x - 0.5f) * Across) + Up * (VCentre + (y - 0.5f) * Tall));
        }

        /// <summary>The rectangle a detector would draw around this body, seen from here.</summary>
        public static View Framing(Body body, P3 camera, Options options = null)
        {
            var o = options ?? new Options();
            var forward = Normalise(body.Centre - camera);
            // Strictly the viewer's LEFT in a left-handed frame, but the ray grid is symmetric about it, so the
            // image is simply mirrored and everything downstream stays self-consistent. Named for its role.
            var right = Normalise(Cross(forward, new P3(0f, 1f, 0f)));
            var up = Cross(right, forward);

            // The rectangle: what a detector would draw. It is axis-aligned in the image and contains the whole
            // object however that object is turned, so it is the bounding rectangle of the eight corners PROJECTED
            // into the image — never a side length. A flat book seen from above is barely 4 cm tall and still fills
            // half the view, because its 35 cm of depth is what projects vertically; sizing the rectangle by
            // `Size.Y` would hand the fitter a thin strip across the book and nothing else.
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;
            for (var corner = 0; corner < 8; corner++)
            {
                var local = new P3(
                    ((corner & 1) == 0 ? -0.5f : 0.5f) * body.Size.X,
                    ((corner & 2) == 0 ? -0.5f : 0.5f) * body.Size.Y,
                    ((corner & 4) == 0 ? -0.5f : 0.5f) * body.Size.Z);
                var world = body.Centre + Turn(local, body.YawDeg) - camera;
                var ahead = Dot(world, forward);
                if (ahead < 0.01f) continue;
                var u = Dot(world, right) / ahead;
                var v = Dot(world, up) / ahead;
                if (u < uMin) uMin = u;
                if (u > uMax) uMax = u;
                if (v < vMin) vMin = v;
                if (v > vMax) vMax = v;
            }
            if (uMin > uMax) return new View(camera, forward, right, up, 0f, 0f, 0f, 0f);
            return new View(camera, forward, right, up,
                (uMin + uMax) * 0.5f, (vMin + vMax) * 0.5f,
                (uMax - uMin) * (1f + o.Margin), (vMax - vMin) * (1f + o.Margin));
        }

        /// <summary>
        /// How far along this ray the first surface is, or 0 for open air. The scene's only sensor: the depth
        /// pipeline reads it through a grid of rays, the old locator reads it through nine, and neither gets to see
        /// anything the other cannot.
        /// </summary>
        public static float Range(Body body, P3 from, P3 direction, Options options = null) =>
            Nearest(body, from, direction, options ?? new Options());

        /// <summary>The patch a headset would hand the fitter, and the viewer it was seen from.</summary>
        public static List<P3> Patch(Body body, P3 camera, Options options = null)
        {
            var o = options ?? new Options();
            var random = new Random(o.Seed);
            var view = Framing(body, camera, o);
            if (view.Across <= 0f) return new List<P3>();
            var points = new List<P3>(o.Pixels * o.Pixels);
            for (var iy = 0; iy < o.Pixels; iy++)
                for (var ix = 0; ix < o.Pixels; ix++)
                {
                    var direction = view.At(ix / (float)(o.Pixels - 1), iy / (float)(o.Pixels - 1));
                    var t = Nearest(body, camera, direction, o);
                    if (t <= 0f) continue;
                    t += (float)(random.NextDouble() - 0.5) * 2f * o.NoiseM;
                    points.Add(camera + direction * t);
                }
            return points;
        }

        /// <summary>Distance to the first thing this ray meets, or 0 for the open air.</summary>
        static float Nearest(Body body, P3 from, P3 direction, Options o)
        {
            var best = float.MaxValue;
            if (!body.Invisible)
            {
                var hit = body.Cylinder ? Cylinder(body, from, direction) : Box(body, from, direction);
                if (hit > 0f) best = Math.Min(best, hit);
            }
            var table = Plane(o.TableY - from.Y, direction.Y);            // the desk top, below the eye
            if (table > 0f) best = Math.Min(best, table);
            var wall = Plane(o.WallZ - from.Z, direction.Z);              // the wall ahead
            if (wall > 0f) best = Math.Min(best, wall);
            return best == float.MaxValue ? 0f : best;
        }

        static float Plane(float gap, float along) => Math.Abs(along) < 1e-5f ? 0f : gap / along > 0f ? gap / along : 0f;

        /// <summary>Slab test in the body's own frame, so a turned box is still a box.</summary>
        static float Box(Body body, P3 from, P3 direction)
        {
            var local = Unturn(from - body.Centre, body.YawDeg);
            var ray = Unturn(direction, body.YawDeg);
            float tMin = 0f, tMax = float.MaxValue;
            if (!Slab(local.X, ray.X, body.Size.X * 0.5f, ref tMin, ref tMax)) return 0f;
            if (!Slab(local.Y, ray.Y, body.Size.Y * 0.5f, ref tMin, ref tMax)) return 0f;
            if (!Slab(local.Z, ray.Z, body.Size.Z * 0.5f, ref tMin, ref tMax)) return 0f;
            return tMin > 0f ? tMin : 0f;
        }

        static bool Slab(float origin, float along, float half, ref float tMin, ref float tMax)
        {
            if (Math.Abs(along) < 1e-6f) return Math.Abs(origin) <= half;
            var t1 = (-half - origin) / along;
            var t2 = (half - origin) / along;
            if (t1 > t2) { var swap = t1; t1 = t2; t2 = swap; }
            tMin = Math.Max(tMin, t1);
            tMax = Math.Min(tMax, t2);
            return tMin <= tMax;
        }

        /// <summary>
        /// An upright cylinder: a circle in plan, capped by its height. Only the SIDE is solid — a ray that clears
        /// the cap passes through, so a bottle viewed steeply from above shows no top. Good enough while the eye is
        /// near the objects' own height, which is where build mode looks.
        /// </summary>
        static float Cylinder(Body body, P3 from, P3 direction)
        {
            var radius = Math.Max(body.Size.X, body.Size.Z) * 0.5f;
            var dx = from.X - body.Centre.X;
            var dz = from.Z - body.Centre.Z;
            var a = direction.X * direction.X + direction.Z * direction.Z;
            if (a < 1e-8f) return 0f;
            var b = 2f * (dx * direction.X + dz * direction.Z);
            var c = dx * dx + dz * dz - radius * radius;
            var disc = b * b - 4f * a * c;
            if (disc < 0f) return 0f;
            var t = (-b - (float)Math.Sqrt(disc)) / (2f * a);
            if (t <= 0f) return 0f;
            var y = from.Y + direction.Y * t;
            return Math.Abs(y - body.Centre.Y) <= body.Size.Y * 0.5f ? t : 0f;
        }

        // The scene turns bodies with the SAME maths the fit uses to read a turn back. Keeping a second copy here is
        // how the two conventions drifted apart once already: the fit reported atan2's angle, the scene built Unity's,
        // and a box came back mirrored.
        static P3 Turn(P3 v, float yawDeg)
        {
            var r = yawDeg * (float)Math.PI / 180f;
            return BoxMath.FromBoxFrame(v.X, v.Y, v.Z, (float)Math.Cos(r), (float)Math.Sin(r));
        }

        static float Dot(P3 a, P3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        static P3 Unturn(P3 v, float yawDeg)
        {
            var r = yawDeg * (float)Math.PI / 180f;
            BoxMath.ToBoxFrame(v, (float)Math.Cos(r), (float)Math.Sin(r), out var u, out var w);
            return new P3(u, v.Y, w);
        }

        static P3 Cross(P3 a, P3 b) => new P3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        static P3 Normalise(P3 v)
        {
            var length = v.Length;
            return length < 1e-6f ? v : v * (1f / length);
        }
    }
}
