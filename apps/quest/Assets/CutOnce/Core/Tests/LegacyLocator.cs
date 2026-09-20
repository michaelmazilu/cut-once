using System;
using System.Collections.Generic;
using CutOnce.Core.Vision;

namespace CutOnce.Core.Tests
{
    /// <summary>
    /// The sizing that shipped, ported line for line from Vision/Object3DLocator.cs so the two can be compared on
    /// identical depth. Nothing here is an improvement on it or a caricature of it: the constants, the 3x3 inner
    /// grid, the 0.4 percentile, the edge-midpoint sizing and the clamps are the ones in the Unity component.
    ///
    /// It is in the test folder because it exists only to be measured against.
    /// </summary>
    public static class LegacyLocator
    {
        public const int SampleGrid = 3;
        public const float SampleSpan = 0.5f;
        public const float MinValidFraction = 0.4f;
        public const float DistancePercentile = 0.4f;
        public const float MinDistance = 0.2f, MaxDistance = 6f;
        public const float MinSizeM = 0.05f, MaxSizeM = 1.2f, MaxDepthM = 0.6f;

        /// <summary>A person is real and taller than furniture-sized clutter; everything else keeps the tight clamp.</summary>
        static float HeightCap(string className) => className == "person" ? 2.0f : MaxSizeM;

        public static bool TryLocate(DepthScene.Body body, P3 camera, string className, DepthScene.Options options,
                                     out P3 world, out P3 size, out string reason)
        {
            world = default;
            size = default;
            reason = "";
            var o = options ?? new DepthScene.Options();
            var view = DepthScene.Framing(body, camera, o);
            if (view.Across <= 0f) { reason = "bad input size"; return false; }

            var centre = view.At(0.5f, 0.5f);
            var forward = view.Forward;

            // Nine samples over the inner half of the box, compared as depth along forward — the one coordinate
            // that means the same thing for rays pointing in different directions.
            var inset = 0.5f - SampleSpan * 0.5f;
            var depths = new List<float>();
            var taken = 0;
            for (var gy = 0; gy < SampleGrid; gy++)
                for (var gx = 0; gx < SampleGrid; gx++)
                {
                    taken++;
                    var tx = SampleGrid == 1 ? 0.5f : gx / (float)(SampleGrid - 1);
                    var ty = SampleGrid == 1 ? 0.5f : gy / (float)(SampleGrid - 1);
                    var direction = view.At(Lerp(inset, 1f - inset, tx), Lerp(inset, 1f - inset, ty));
                    var t = DepthScene.Range(body, camera, direction, o);
                    if (t <= 0f) continue;
                    var hit = camera + direction * t;
                    var depth = Dot(hit - camera, forward);
                    if (depth < MinDistance || depth > MaxDistance) continue;
                    depths.Add(depth);
                }

            if (depths.Count == 0 || depths.Count < taken * MinValidFraction)
            {
                reason = $"only {depths.Count}/{taken} depth samples valid";
                return false;
            }

            depths.Sort();
            var index = Clamp((int)Math.Round((depths.Count - 1) * (double)DistancePercentile), 0, depths.Count - 1);
            var depthAt = depths[index];

            var cosine = Dot(centre, forward);
            if (cosine < 0.1f) { reason = "detection too far off-axis to place"; return false; }
            var along = depthAt / cosine;
            world = camera + centre * along;
            size = SizeAt(view, along, HeightCap(className));
            return true;
        }

        /// <summary>
        /// The box's world size at a distance along the view: rays through the edge midpoints, cut at that distance,
        /// measured against each other. The depth axis is the smaller footprint axis, capped harder.
        /// </summary>
        static P3 SizeAt(DepthScene.View view, float distance, float maxHeight)
        {
            var left = view.Camera + view.At(0f, 0.5f) * distance;
            var right = view.Camera + view.At(1f, 0.5f) * distance;
            var top = view.Camera + view.At(0.5f, 0f) * distance;
            var bottom = view.Camera + view.At(0.5f, 1f) * distance;
            var width = Clamp((right - left).Length, MinSizeM, MaxSizeM);
            var height = Clamp((bottom - top).Length, MinSizeM, maxHeight);
            var depth = Clamp(Math.Min(width, height), MinSizeM, MaxDepthM);
            return new P3(width, height, depth);
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        static float Dot(P3 a, P3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        static float Clamp(float v, float low, float high) => v < low ? low : v > high ? high : v;
        static int Clamp(int v, int low, int high) => v < low ? low : v > high ? high : v;
    }
}
