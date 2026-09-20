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
            public float NoiseM = 0.004f;     // depth noise, one sigma
            public int Seed = 7;
        }

        /// <summary>The patch a headset would hand the fitter, and the viewer it was seen from.</summary>
        public static List<P3> Patch(Body body, P3 camera, Options options = null)
        {
            var o = options ?? new Options();
            var random = new Random(o.Seed);
            var forward = Normalise(body.Centre - camera);
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
            if (uMin > uMax) return new List<P3>();
            var uCentre = (uMin + uMax) * 0.5f;
            var vCentre = (vMin + vMax) * 0.5f;
            var across = (uMax - uMin) * (1f + o.Margin);
            var tall = (vMax - vMin) * (1f + o.Margin);

            var points = new List<P3>(o.Pixels * o.Pixels);
            for (var iy = 0; iy < o.Pixels; iy++)
                for (var ix = 0; ix < o.Pixels; ix++)
                {
                    var u = uCentre + (ix / (float)(o.Pixels - 1) - 0.5f) * across;
                    var v = vCentre + (iy / (float)(o.Pixels - 1) - 0.5f) * tall;
                    var direction = Normalise(forward + right * u + up * v);
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

        /// <summary>An upright cylinder: a circle in plan, capped by its height.</summary>
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

        static P3 Turn(P3 v, float yawDeg)
        {
            var r = yawDeg * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(r), sin = (float)Math.Sin(r);
            return new P3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos);
        }

        static float Dot(P3 a, P3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        static P3 Unturn(P3 v, float yawDeg)
        {
            var r = -yawDeg * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(r), sin = (float)Math.Sin(r);
            return new P3(v.X * cos + v.Z * sin, v.Y, -v.X * sin + v.Z * cos);
        }

        static P3 Cross(P3 a, P3 b) => new P3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        static P3 Normalise(P3 v)
        {
            var length = v.Length;
            return length < 1e-6f ? v : v * (1f / length);
        }
    }
}
