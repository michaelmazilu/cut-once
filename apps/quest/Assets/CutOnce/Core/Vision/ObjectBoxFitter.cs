using System;
using System.Collections.Generic;

namespace CutOnce.Core.Vision
{
    /// <summary>How hard the fit works before it gives up. Metres throughout.</summary>
    public sealed class FitOptions
    {
        /// <summary>
        /// Cluster cell at one metre. Depth samples spread apart with distance — the same grid of rays lands 2 cm
        /// apart on a desk and 6 cm apart across the room — so the cell grows with the object's distance, or a far
        /// surface shatters into a hundred clusters none of which is big enough to measure.
        /// </summary>
        public float VoxelAtOneMetreM = 0.02f;
        public float VoxelMinM = 0.015f, VoxelMaxM = 0.08f;
        /// <summary>
        /// The table an object stands on is physically continuous with it, so clustering alone would measure both.
        /// The dominant flat surface is dropped first, as the server's scan does: objects are what stands above it.
        /// </summary>
        public bool RemoveSupport = true;
        /// <summary>The height of the surface the object stands on, when something already knows it (an MRUK table plane). Guessed from the patch when null.</summary>
        public float? SupportY = null;
        /// <summary>Half-thickness of the removed slab. Wider than depth noise, so the table goes; anything thinner than this, lying flat, goes with it.</summary>
        public float SupportToleranceM = 0.012f;
        /// <summary>The flat surface must hold this share of the patch before it counts as the support.</summary>
        public float SupportMinShare = 0.18f;
        /// <summary>
        /// An object whose lowest measured point is within this of the support is RESTING on it, so its box reaches
        /// down to the surface. Without this every flat thing reads about half its true thickness and floats: the
        /// slab that removes the table also removes the object's bottom millimetres, and what is left is the top.
        /// </summary>
        public float SupportSnapM = 0.06f;
        /// <summary>
        /// The cell must be wider than the gap between neighbouring samples, or a surface is not one cluster but a
        /// hundred, and the fit measures a shard. Depth samples spread out with distance AND with the object's size
        /// — a detector's box always holds about the same number of them — so the cell is taken from the cloud
        /// itself, and the distance rule below is only a floor.
        /// </summary>
        public float VoxelPerSpacing = 1.8f;
        /// <summary>Fewer points than this and nothing is measured: a box from five samples is a guess wearing a box.</summary>
        public int MinPoints = 20;
        /// <summary>A cell with fewer than this many occupied neighbours (of 26) is speckle, not surface.</summary>
        public int MinNeighbourCells = 2;
        /// <summary>Extents are read between these percentiles, never min/max: one stray point cannot stretch a box.</summary>
        public float TrimLow = 0.02f, TrimHigh = 0.98f;
        /// <summary>Points further than this from the viewer are another room, not this object.</summary>
        public float MaxReachM = 5f;
        /// <summary>The chosen cluster must hold this share of the patch, or the detection was mostly background.</summary>
        public float MinShare = 0.10f;
        /// <summary>Points for full geometric confidence.</summary>
        public float PointsForFullConfidence = 60f;
    }

    /// <summary>
    /// What a class of object may plausibly measure, as ranges on its sorted dimensions. A sanity check only: geometry
    /// that falls outside is REJECTED, never squeezed into the range — a box that was forced to fit is a lie the
    /// headset then draws at full confidence.
    /// </summary>
    public readonly struct SizePrior
    {
        public readonly string Name;
        /// <summary>
        /// Ranges on the three dimensions once sorted. All three are needed: longest and shortest alone cannot tell a
        /// 32 × 4 × 32 cm slab from a bottle, because both have one long side and one thin one. The middle can.
        /// </summary>
        public readonly float LongestMinM, LongestMaxM, MidMinM, MidMaxM, ShortestMinM, ShortestMaxM;
        /// <summary>
        /// Round in plan (a bottle, a cup, an apple). One viewpoint only ever sees an object's near half, so its extent
        /// ALONG the view collapses to about half the truth. For a round thing that half is recoverable: the footprint
        /// is a circle, so the unseen depth equals the width that was measured across the view.
        /// </summary>
        public readonly bool Round;

        public SizePrior(string name, float longestMin, float longestMax, float midMin, float midMax, float shortestMin, float shortestMax, bool round = false)
        {
            Name = name;
            LongestMinM = longestMin; LongestMaxM = longestMax;
            MidMinM = midMin; MidMaxM = midMax;
            ShortestMinM = shortestMin; ShortestMaxM = shortestMax;
            Round = round;
        }

        /// <summary>Anything the table does not know: permissive, so an unknown object still gets a measured box.</summary>
        public static SizePrior Unknown => new SizePrior("unknown", 0.02f, 1.2f, 0.01f, 1.2f, 0.005f, 1.2f);
    }

    /// <summary>
    /// The size ranges and the classes build mode ignores. COCO names, as the on-device model emits them.
    /// </summary>
    public static class SizePriors
    {
        static readonly Dictionary<string, SizePrior> Table = new Dictionary<string, SizePrior>(StringComparer.OrdinalIgnoreCase)
        {
            //                                            longest        middle         shortest
            { "bottle",      new SizePrior("bottle",      0.10f, 0.40f, 0.03f, 0.15f, 0.03f, 0.15f, round: true) },
            { "cup",         new SizePrior("cup",         0.06f, 0.20f, 0.04f, 0.15f, 0.04f, 0.15f, round: true) },
            { "wine glass",  new SizePrior("wine glass",  0.10f, 0.25f, 0.04f, 0.12f, 0.04f, 0.12f, round: true) },
            { "laptop",      new SizePrior("laptop",      0.20f, 0.45f, 0.15f, 0.35f, 0.01f, 0.06f) },
            { "keyboard",    new SizePrior("keyboard",    0.20f, 0.50f, 0.08f, 0.25f, 0.01f, 0.06f) },
            { "mouse",       new SizePrior("mouse",       0.05f, 0.15f, 0.03f, 0.09f, 0.02f, 0.06f) },
            { "cell phone",  new SizePrior("cell phone",  0.10f, 0.20f, 0.05f, 0.10f, 0.004f, 0.02f) },
            { "book",        new SizePrior("book",        0.12f, 0.45f, 0.10f, 0.40f, 0.005f, 0.08f) },
            { "remote",      new SizePrior("remote",      0.08f, 0.25f, 0.03f, 0.08f, 0.01f, 0.05f) },
            { "scissors",    new SizePrior("scissors",    0.10f, 0.25f, 0.04f, 0.12f, 0.005f, 0.03f) },
            { "banana",      new SizePrior("banana",      0.10f, 0.25f, 0.03f, 0.08f, 0.02f, 0.06f) },
            { "apple",       new SizePrior("apple",       0.05f, 0.12f, 0.04f, 0.12f, 0.04f, 0.12f, round: true) },
            { "orange",      new SizePrior("orange",      0.05f, 0.12f, 0.04f, 0.12f, 0.04f, 0.12f, round: true) },
            { "vase",        new SizePrior("vase",        0.08f, 0.45f, 0.05f, 0.25f, 0.05f, 0.25f, round: true) },
            { "backpack",    new SizePrior("backpack",    0.25f, 0.60f, 0.15f, 0.40f, 0.10f, 0.35f) },
            { "clock",       new SizePrior("clock",       0.05f, 0.40f, 0.05f, 0.40f, 0.02f, 0.15f) },
        };

        /// <summary>
        /// Classes build mode never boxes. People first: a room full of teammates is a room full of person-sized
        /// holograms, and a box around a judge reads as surveillance, not as a build assistant. The rest are the room
        /// itself — furniture is not build material, and its boxes are the ones that swallow the view.
        /// </summary>
        static readonly HashSet<string> Never = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "person", "chair", "couch", "bed", "dining table", "toilet", "tv", "refrigerator", "oven", "microwave",
            "sink", "door", "bench", "potted plant", "car", "bus", "train", "truck", "boat", "aeroplane", "bicycle", "motorbike",
        };

        public static bool Ignored(string className) => className != null && Never.Contains(className);

        public static SizePrior For(string className)
        {
            if (className != null && Table.TryGetValue(className, out var prior)) return prior;
            return SizePrior.Unknown;
        }
    }

    /// <summary>A measured box, or the reason there isn't one.</summary>
    public readonly struct FitResult
    {
        public readonly bool Ok;
        public readonly P3 Centre;
        /// <summary>Turn about the room's up axis, in (-45, 45]: a box is never tipped, only turned.</summary>
        public readonly float YawDeg;
        public readonly P3 Size;
        /// <summary>How much the GEOMETRY is trusted, apart from how much the class is: points, and how much of the patch was the object.</summary>
        public readonly float Confidence;
        public readonly int Points;
        public readonly string Reason;

        FitResult(bool ok, P3 centre, float yaw, P3 size, float confidence, int points, string reason)
        {
            Ok = ok; Centre = centre; YawDeg = yaw; Size = size; Confidence = confidence; Points = points; Reason = reason;
        }

        public static FitResult Fitted(P3 centre, float yaw, P3 size, float confidence, int points) =>
            new FitResult(true, centre, yaw, size, confidence, points, null);
        public static FitResult Rejected(string reason, int points = 0) =>
            new FitResult(false, default, 0f, default, 0f, points, reason);

        public override string ToString() => Ok
            ? $"{Size.X * 100:0.#} × {Size.Y * 100:0.#} × {Size.Z * 100:0.#} cm at {Centre}, yaw {YawDeg:0.#}°, confidence {Confidence:0.00} ({Points} pts)"
            : $"rejected: {Reason}";
    }

    /// <summary>
    /// Measures a real object's box from the depth points inside a detection, instead of inferring one from the
    /// detection's rectangle and a single distance.
    ///
    /// Why the rebuild: a rectangle always holds background, and size-from-one-distance multiplies every depth error
    /// into every axis — read a bottle's depth off the wall behind it and its box grows by the ratio, cubed in volume.
    /// Here the background is not filtered by a percentile, it is a DIFFERENT CLUSTER and never enters the fit.
    ///
    /// Pipeline: valid points → voxel clusters → the nearest substantial cluster → drop speckle → gravity-constrained
    /// oriented box (yaw from the footprint's smallest enclosing rectangle, extents between percentiles) → a class
    /// plausibility check that rejects rather than clamps → a geometric confidence separate from the detector's.
    ///
    /// Pure C#: it runs in the headset, in the fast test runner, and against recorded captures on a laptop, unchanged.
    /// </summary>
    public static class ObjectBoxFitter
    {
        static readonly FitOptions Defaults = new FitOptions();

        public static FitResult Fit(IReadOnlyList<P3> patch, P3 viewer, SizePrior prior, FitOptions options = null)
        {
            var o = options ?? Defaults;
            if (patch == null || patch.Count == 0) return FitResult.Rejected("no depth in the detection");

            // 1. Only real, reachable points.
            var valid = new List<P3>(patch.Count);
            foreach (var p in patch)
            {
                if (!p.IsFinite) continue;
                var reach = (p - viewer).Length;
                if (reach < 0.05f || reach > o.MaxReachM) continue;
                valid.Add(p);
            }
            if (valid.Count < o.MinPoints) return FitResult.Rejected($"only {valid.Count} usable depth points", valid.Count);

            // 2. Drop the surface the object stands on, or clustering measures the desk and the object as one thing.
            float? support = null;
            if (o.RemoveSupport)
            {
                var standing = WithoutSupport(valid, o, out var planeY);
                if (planeY.HasValue)
                {
                    // What is left after the desk goes must be a real remainder. A phone 8 mm thick lying on a table
                    // does not clear the slab, and the handful of noisy edge samples that survive are not the phone.
                    // Measuring them yields a confident little box in the wrong place; falling back to the whole
                    // patch yields a table-sized one. Both are worse than saying so.
                    var substantial = Math.Max(o.MinPoints, (int)(valid.Count * o.MinShare));
                    if (standing.Count < substantial)
                        return FitResult.Rejected("could not be told apart from the surface it lies on", standing.Count);
                    valid = standing;
                    support = planeY;
                }
            }

            // 3. Voxel clusters: touching cells are one surface, a gap splits the object from the wall behind it.
            //    The cell is the larger of what distance implies and what the samples actually are.
            var voxel = Clamp(
                Math.Max(o.VoxelAtOneMetreM * MedianDistance(valid, viewer), o.VoxelPerSpacing * SampleSpacing(valid)),
                o.VoxelMinM, o.VoxelMaxM);
            var cells = new Dictionary<long, List<int>>();
            var keys = new long[valid.Count];
            for (var i = 0; i < valid.Count; i++)
            {
                var key = CellKey(valid[i], voxel);
                keys[i] = key;
                if (!cells.TryGetValue(key, out var bucket)) cells[key] = bucket = new List<int>();
                bucket.Add(i);
            }
            var clusters = Clusters(cells);

            // 3. The nearest cluster that is more than speckle. Not the largest: a wall fills more of a rectangle than
            //    the object in front of it does, every time.
            List<int> chosen = null;
            var chosenDistance = float.MaxValue;
            var needed = Math.Max(o.MinPoints, (int)(valid.Count * o.MinShare));
            foreach (var cluster in clusters)
            {
                if (cluster.Count < needed) continue;
                var distance = MeanDistance(valid, cluster, viewer);
                if (distance >= chosenDistance) continue;
                chosen = cluster;
                chosenDistance = distance;
            }
            if (chosen == null) return FitResult.Rejected($"no surface in the detection held {needed} points", valid.Count);

            // 4. Speckle: a cell with almost no occupied neighbours is a depth artefact, not part of a surface.
            var kept = new List<P3>(chosen.Count);
            foreach (var i in chosen)
                if (NeighbourCells(cells, keys[i]) >= o.MinNeighbourCells) kept.Add(valid[i]);
            if (kept.Count < o.MinPoints) return FitResult.Rejected($"only {kept.Count} points survived the speckle filter", kept.Count);

            // 5. The box. Height from the up axis, turn and footprint from the smallest enclosing rectangle.
            var ys = new List<float>(kept.Count);
            foreach (var p in kept) ys.Add(p.Y);
            ys.Sort();
            var yLow = BoxMath.Percentile(ys, o.TrimLow);
            var yHigh = BoxMath.Percentile(ys, o.TrimHigh);

            // Something standing on a surface reaches down to it. Measuring only what survived the support cut reads
            // a laptop as 1 cm thick and floats it half a centimetre above the desk.
            if (support.HasValue && yHigh > support.Value && yLow - support.Value < o.SupportSnapM)
                yLow = support.Value;

            var yaw = BoxMath.MinAreaYawDeg(BoxMath.HullXz(kept), kept, voxel);
            var radians = yaw * (float)Math.PI / 180f;
            float cos = (float)Math.Cos(radians), sin = (float)Math.Sin(radians);
            var us = new List<float>(kept.Count);
            var vs = new List<float>(kept.Count);
            foreach (var p in kept)
            {
                BoxMath.ToBoxFrame(p, cos, sin, out var u, out var v);
                us.Add(u);
                vs.Add(v);
            }
            us.Sort(); vs.Sort();
            float uLow = BoxMath.Percentile(us, o.TrimLow), uHigh = BoxMath.Percentile(us, o.TrimHigh);
            float vLow = BoxMath.Percentile(vs, o.TrimLow), vHigh = BoxMath.Percentile(vs, o.TrimHigh);

            var uMid = (uLow + uHigh) * 0.5f;
            var vMid = (vLow + vHigh) * 0.5f;
            var centre = BoxMath.FromBoxFrame(uMid, (yLow + yHigh) * 0.5f, vMid, cos, sin);
            var size = new P3(Math.Max(uHigh - uLow, 0.005f), Math.Max(yHigh - yLow, 0.005f), Math.Max(vHigh - vLow, 0.005f));

            // One viewpoint sees an object's near half only, so the extent along the view is short by about half. For a
            // class that is round in plan the missing half is not a guess: a circle's footprint is as deep as it is wide.
            if (prior.Round)
            {
                var widest = Math.Max(size.X, size.Z);
                // Widening alone would grow the box backwards AND forwards, leaving its centre on the near surface.
                // The centre belongs at the circle's centre, half the missing depth further from the eye.
                var shortfall = widest - Math.Min(size.X, size.Z);
                var awayX = centre.X - viewer.X;
                var awayZ = centre.Z - viewer.Z;
                var flat = (float)Math.Sqrt((double)awayX * awayX + (double)awayZ * awayZ);
                if (flat > 1e-4f)
                    centre = new P3(centre.X + awayX / flat * shortfall * 0.5f, centre.Y, centre.Z + awayZ / flat * shortfall * 0.5f);
                size = new P3(widest, size.Y, widest);
            }

            // 6. Plausible for this class? Rejected, never resized: a box squeezed into a prior is fiction drawn at
            //    full confidence, which is exactly what the old size clamp produced.
            var sorted = new[] { size.X, size.Y, size.Z };
            Array.Sort(sorted);
            float shortest = sorted[0], middle = sorted[1], longest = sorted[2];
            if (longest < prior.LongestMinM || longest > prior.LongestMaxM)
                return FitResult.Rejected($"{longest * 100:0.#} cm across is not a {prior.Name} ({prior.LongestMinM * 100:0.#}–{prior.LongestMaxM * 100:0.#} cm)", kept.Count);
            if (middle < prior.MidMinM || middle > prior.MidMaxM)
                return FitResult.Rejected($"{middle * 100:0.#} cm wide is not a {prior.Name} ({prior.MidMinM * 100:0.#}–{prior.MidMaxM * 100:0.#} cm)", kept.Count);
            if (shortest < prior.ShortestMinM || shortest > prior.ShortestMaxM)
                return FitResult.Rejected($"{shortest * 100:0.#} cm thick is not a {prior.Name} ({prior.ShortestMinM * 100:0.#}–{prior.ShortestMaxM * 100:0.#} cm)", kept.Count);

            // 7. Geometric confidence: how much was measured, and how much of the detection was actually this object.
            var share = (float)kept.Count / valid.Count;
            var density = BoxMath.Clamp01(kept.Count / o.PointsForFullConfidence);
            return FitResult.Fitted(centre, yaw, size, BoxMath.Clamp01(density * (0.35f + 0.65f * share)), kept.Count);
        }

        /// <summary>
        /// The patch without its dominant flat surface. Heights are binned; a bin holding a large share of the patch
        /// is a table, a desk or the floor, and everything within a slab of it is removed. Only horizontal planes are
        /// considered: a wall is not a support, and it is dropped later by being the far cluster instead.
        /// </summary>
        static List<P3> WithoutSupport(List<P3> points, FitOptions o, out float? planeY)
        {
            float plane;
            if (o.SupportY.HasValue)
            {
                plane = o.SupportY.Value;                    // MRUK knows the table: no need to guess
            }
            else
            {
                // The LOWEST substantial flat run, not the most populous one. A book filling the detection is the
                // biggest flat thing in it; the desk it lies on is the one underneath.
                var bin = Math.Max(o.SupportToleranceM, 0.005f);
                var counts = new Dictionary<int, int>();
                foreach (var p in points)
                {
                    var key = (int)Math.Floor(p.Y / bin);
                    counts.TryGetValue(key, out var seen);
                    counts[key] = seen + 1;
                }
                var needed = points.Count * o.SupportMinShare;
                var lowest = int.MaxValue;
                foreach (var pair in counts)
                    if (pair.Value >= needed && pair.Key < lowest) lowest = pair.Key;
                if (lowest == int.MaxValue) { planeY = null; return points; }   // nothing flat enough to be a support
                plane = (lowest + 0.5f) * bin;
            }

            planeY = plane;
            var standing = new List<P3>(points.Count);
            foreach (var p in points)
                if (Math.Abs(p.Y - plane) > o.SupportToleranceM) standing.Add(p);
            return standing;
        }

        /// <summary>
        /// How far apart neighbouring samples are, as the median nearest-neighbour gap over a subsample. Sampled
        /// rather than exhaustive: a few dozen probes settle the scale, and the cost stays flat as the cloud grows.
        /// </summary>
        static float SampleSpacing(List<P3> points)
        {
            const int probes = 48, against = 220;
            var n = points.Count;
            if (n < 4) return 0f;
            var step = Math.Max(1, n / probes);
            var other = Math.Max(1, n / Math.Min(n, against));
            var gaps = new List<float>();
            for (var i = 0; i < n; i += step)
            {
                var nearest = float.MaxValue;
                for (var j = 0; j < n; j += other)
                {
                    if (j == i) continue;
                    var d = (points[j] - points[i]).Length;
                    if (d < nearest) nearest = d;
                }
                if (nearest < float.MaxValue) gaps.Add(nearest);
            }
            if (gaps.Count == 0) return 0f;
            gaps.Sort();
            return BoxMath.Percentile(gaps, 0.5f);
        }

        static float MedianDistance(List<P3> points, P3 viewer)
        {
            var distances = new List<float>(points.Count);
            foreach (var p in points) distances.Add((p - viewer).Length);
            distances.Sort();
            return BoxMath.Percentile(distances, 0.5f);
        }

        static float Clamp(float v, float low, float high) => v < low ? low : v > high ? high : v;

        static long CellKey(P3 p, float voxel)
        {
            var x = (long)Math.Floor(p.X / voxel);
            var y = (long)Math.Floor(p.Y / voxel);
            var z = (long)Math.Floor(p.Z / voxel);
            return ((x & 0x1FFFFF) << 42) | ((y & 0x1FFFFF) << 21) | (z & 0x1FFFFF);
        }

        static long Shift(long key, int dx, int dy, int dz)
        {
            var x = ((key >> 42) & 0x1FFFFF) + dx;
            var y = ((key >> 21) & 0x1FFFFF) + dy;
            var z = (key & 0x1FFFFF) + dz;
            return ((x & 0x1FFFFF) << 42) | ((y & 0x1FFFFF) << 21) | (z & 0x1FFFFF);
        }

        /// <summary>Flood fill over occupied cells, 26-connected: one list of point indices per connected surface.</summary>
        static List<List<int>> Clusters(Dictionary<long, List<int>> cells)
        {
            var seen = new HashSet<long>();
            var clusters = new List<List<int>>();
            var stack = new Stack<long>();
            foreach (var start in cells.Keys)
            {
                if (seen.Contains(start)) continue;
                var cluster = new List<int>();
                stack.Push(start);
                seen.Add(start);
                while (stack.Count > 0)
                {
                    var key = stack.Pop();
                    cluster.AddRange(cells[key]);
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dy = -1; dy <= 1; dy++)
                            for (var dz = -1; dz <= 1; dz++)
                            {
                                if (dx == 0 && dy == 0 && dz == 0) continue;
                                var next = Shift(key, dx, dy, dz);
                                if (seen.Contains(next) || !cells.ContainsKey(next)) continue;
                                seen.Add(next);
                                stack.Push(next);
                            }
                }
                clusters.Add(cluster);
            }
            return clusters;
        }

        static int NeighbourCells(Dictionary<long, List<int>> cells, long key)
        {
            var count = 0;
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    for (var dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) continue;
                        if (cells.ContainsKey(Shift(key, dx, dy, dz))) count++;
                    }
            return count;
        }

        static float MeanDistance(List<P3> points, List<int> cluster, P3 viewer)
        {
            double total = 0;
            foreach (var i in cluster) total += (points[i] - viewer).Length;
            return (float)(total / cluster.Count);
        }
    }
}
