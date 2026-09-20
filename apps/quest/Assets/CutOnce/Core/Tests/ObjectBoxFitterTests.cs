using System;
using System.Collections.Generic;
using CutOnce.Core.Vision;
using NUnit.Framework;

namespace CutOnce.Core.Tests
{
    /// <summary>
    /// The box pipeline against simulated depth. Every case puts the object in front of a wall and on a table, with a
    /// detection rectangle that spills past it, because that is the case the old size-from-one-distance path turned
    /// into a metre-wide cube.
    /// </summary>
    public class ObjectBoxFitterTests
    {
        static readonly P3 Eye = new P3(0f, 1.35f, -0.9f);   // seated, looking at the desk

        static DepthScene.Body Bottle(float z = 0.2f) => new DepthScene.Body
        { Centre = new P3(0f, 0.74f + 0.125f, z), Size = new P3(0.07f, 0.25f, 0.07f), Cylinder = true };

        static FitResult FitOf(DepthScene.Body body, string className, P3 eye, DepthScene.Options options = null)
        {
            var patch = DepthScene.Patch(body, eye, options);
            return ObjectBoxFitter.Fit(patch, eye, SizePriors.For(className));
        }

        static void AssertSize(FitResult fit, P3 truth, float toleranceM, string what)
        {
            Assert.That(fit.Ok, Is.True, $"{what}: {fit.Reason}");
            Assert.That(fit.Size.X, Is.EqualTo(truth.X).Within(toleranceM), $"{what}: width");
            Assert.That(fit.Size.Y, Is.EqualTo(truth.Y).Within(toleranceM), $"{what}: height");
            Assert.That(fit.Size.Z, Is.EqualTo(truth.Z).Within(toleranceM), $"{what}: depth");
        }

        [Test]
        public void MeasuresABottleAtEveryDistance()
        {
            foreach (var z in new[] { 0.0f, 0.6f, 1.6f })
            {
                var body = Bottle(z);
                var fit = FitOf(body, "bottle", Eye);
                AssertSize(fit, body.Size, 0.03f, $"bottle at z={z}");
                Assert.That((fit.Centre - body.Centre).Length, Is.LessThan(0.03f), $"bottle at z={z}: centre");
            }
        }

        [Test]
        public void TheWallBehindNeverEntersTheBox()
        {
            // The old path read one distance through the rectangle; when that distance was the wall, the box grew by
            // the ratio. Here the wall is 3 m away, the bottle 1 m: the wall is a different cluster and is dropped.
            var fit = FitOf(Bottle(), "bottle", Eye, new DepthScene.Options { WallZ = 3f, Margin = 0.8f });
            Assert.That(fit.Ok, Is.True, fit.Reason);
            Assert.That(Math.Max(fit.Size.X, Math.Max(fit.Size.Y, fit.Size.Z)), Is.LessThan(0.35f),
                "a bottle in front of a far wall must stay bottle-sized");
        }

        [Test]
        public void FindsTheTurnOfABoxOnTheTable()
        {
            var body = new DepthScene.Body
            { Centre = new P3(0f, 0.74f + 0.02f, 0.25f), Size = new P3(0.35f, 0.04f, 0.35f), YawDeg = 30f };
            var fit = FitOf(body, "book", new P3(0f, 1.5f, -0.75f));
            Assert.That(fit.Ok, Is.True, fit.Reason);
            Assert.That(Math.Abs(BoxMath.DeltaYaw(30f, fit.YawDeg)), Is.LessThan(12f), $"yaw was {fit.YawDeg:0.#}°");
            Assert.That(Math.Max(fit.Size.X, fit.Size.Z), Is.EqualTo(0.35f).Within(0.06f), "footprint");
        }

        [Test]
        public void RejectsWhatItCannotSee()
        {
            // Clear plastic: the sensor sees the table and the wall through it. The honest answer is no box at all,
            // not a box around the desk.
            var body = Bottle();
            body.Invisible = true;
            var fit = FitOf(body, "bottle", Eye);
            Assert.That(fit.Ok, Is.False, "a bottle the sensor sees through must not be boxed");
            // All that came back was the desk, and the reason says exactly that rather than reporting a desk-sized
            // bottle that happened to fail a size check.
            Assert.That(fit.Reason, Does.Contain("surface"));
        }

        [Test]
        public void RejectsInsteadOfSqueezingIntoThePrior()
        {
            // A laptop-sized thing called a bottle: the geometry is plausible, the label is not. Rejected, never resized.
            var body = new DepthScene.Body { Centre = new P3(0f, 0.75f, 0.2f), Size = new P3(0.32f, 0.02f, 0.22f) };
            var fit = FitOf(body, "bottle", new P3(0f, 1.5f, -0.7f));
            Assert.That(fit.Ok, Is.False);
            Assert.That(fit.Reason, Does.Contain("not a bottle"));
        }

        [Test]
        public void PeopleAndFurnitureAreNeverBoxed()
        {
            Assert.That(SizePriors.Ignored("person"), Is.True);
            foreach (var room in new[] { "chair", "dining table", "tv", "couch", "refrigerator" })
                Assert.That(SizePriors.Ignored(room), Is.True, room);
            foreach (var material in new[] { "bottle", "laptop", "cup", "book" })
                Assert.That(SizePriors.Ignored(material), Is.False, material);
        }

        [Test]
        public void SurvivesNoisyDepth()
        {
            var body = Bottle();
            var fit = FitOf(body, "bottle", Eye, new DepthScene.Options { NoiseM = 0.012f, Seed = 11 });
            AssertSize(fit, body.Size, 0.05f, "noisy bottle");
        }

        [Test]
        public void SaysWhyWhenThereIsNothingToMeasure()
        {
            var fit = ObjectBoxFitter.Fit(new List<P3>(), Eye, SizePriors.For("bottle"));
            Assert.That(fit.Ok, Is.False);
            Assert.That(fit.Reason, Is.EqualTo("no depth in the detection"));
        }

        // ── temporal ────────────────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void DimensionsStopBreathing()
        {
            var smoother = new BoxSmoother();
            var body = Bottle();
            var random = new Random(3);
            var widths = new List<float>();
            for (var frame = 0; frame < 12; frame++)
            {
                var eye = Eye + new P3((float)(random.NextDouble() - 0.5) * 0.04f, 0f, (float)(random.NextDouble() - 0.5) * 0.04f);
                smoother.Update(FitOf(body, "bottle", eye, new DepthScene.Options { NoiseM = 0.008f, Seed = frame }), 1f / 3f);
                if (frame >= 6) widths.Add(smoother.Size.X);
            }
            float min = float.MaxValue, max = float.MinValue;
            foreach (var w in widths) { min = Math.Min(min, w); max = Math.Max(max, w); }
            Assert.That(max - min, Is.LessThan(0.01f), "the held width wobbled by more than a centimetre");
            Assert.That(smoother.Stable, Is.True, "a still object should settle");
        }

        [Test]
        public void AFullBoxOnlyAfterSeveralAgreeingFits()
        {
            var smoother = new BoxSmoother();
            var body = Bottle();
            Assert.That(smoother.HasBox, Is.False);
            smoother.Update(FitOf(body, "bottle", Eye, new DepthScene.Options { Seed = 1 }), 1f / 3f);
            Assert.That(smoother.HasBox, Is.True, "the first fit gives a box…");
            Assert.That(smoother.Stable, Is.False, "…but not yet a confident one");
            smoother.Update(FitOf(body, "bottle", Eye, new DepthScene.Options { Seed = 2 }), 1f / 3f);
            smoother.Update(FitOf(body, "bottle", Eye, new DepthScene.Options { Seed = 3 }), 1f / 3f);
            Assert.That(smoother.Stable, Is.True);
        }

        [Test]
        public void ABoxBornInTheWrongPlaceRecovers()
        {
            // The trap in a pure jump gate: an object born on the wall rejects every correct measurement afterwards,
            // because the truth is exactly what looks like a jump. Three fits that agree with each other win.
            var smoother = new BoxSmoother();
            var truth = Bottle();
            var wrong = FitResult.Fitted(new P3(0f, 1.0f, 2.9f), 0f, new P3(0.1f, 0.3f, 0.1f), 0.5f, 40);
            smoother.Update(wrong, 1f / 3f);
            Assert.That((smoother.Centre - truth.Centre).Length, Is.GreaterThan(1f), "born on the wall");

            for (var frame = 0; frame < 3; frame++)
                smoother.Update(FitOf(truth, "bottle", Eye, new DepthScene.Options { Seed = frame }), 1f / 3f);

            Assert.That((smoother.Centre - truth.Centre).Length, Is.LessThan(0.05f), "it should have come back to the bottle");
        }

        [Test]
        public void AMissDecaysConfidenceWithoutMovingTheBox()
        {
            var smoother = new BoxSmoother();
            var body = Bottle();
            for (var i = 0; i < 3; i++) smoother.Update(FitOf(body, "bottle", Eye, new DepthScene.Options { Seed = i }), 1f / 3f);
            var held = smoother.Centre;
            var confidence = smoother.GeometryConfidence;
            smoother.Update(FitResult.Rejected("nothing to measure"), 1f / 3f);
            Assert.That((smoother.Centre - held).Length, Is.LessThan(1e-4f), "a miss must not move the box");
            Assert.That(smoother.GeometryConfidence, Is.LessThan(confidence), "a miss must cost confidence");
        }

        // ── the whole table, printed for inspection ─────────────────────────────────────────────────────────────
        [Test]
        public void OneFitCostsLessThanAFrame()
        {
            // The scan runs a few times a second over a dozen detections. What matters is that a fit is small next to
            // a 13.9 ms frame, and that it does not grow with the cloud the way an exhaustive nearest-neighbour would.
            var patches = new List<List<P3>>();
            foreach (var z in new[] { 0.0f, 0.6f, 1.6f })
                patches.Add(DepthScene.Patch(Bottle(z), Eye, new DepthScene.Options { Pixels = 32 }));

            var prior = SizePriors.For("bottle");
            foreach (var patch in patches) ObjectBoxFitter.Fit(patch, Eye, prior);   // warm the JIT

            var clock = System.Diagnostics.Stopwatch.StartNew();
            const int rounds = 200;
            for (var i = 0; i < rounds; i++)
                foreach (var patch in patches)
                    ObjectBoxFitter.Fit(patch, Eye, prior);
            clock.Stop();

            var each = clock.Elapsed.TotalMilliseconds / (rounds * patches.Count);
            Console.WriteLine($"\n  one fit over a 32x32 patch: {each:0.000} ms  ({patches[0].Count} points)\n");
            Assert.That(each, Is.LessThan(5.0), $"a single fit took {each:0.00} ms");
        }

        sealed class Case
        {
            public string Name;
            public string ClassName;
            public DepthScene.Body Body;
            public P3 Eye;
            public DepthScene.Options Options;
            /// <summary>How close the measurement must be on every axis, in metres. Null means it must be REJECTED.</summary>
            public float? ToleranceM;
        }

        /// <summary>
        /// Every object the pipeline is meant to handle, measured end to end and printed as a table: bottles, a cup,
        /// a laptop, a phone, a tool, a turned book, a bag, at reaches from 1 to 4 metres, plus the cases that must
        /// NOT produce a box — glass the sensor sees through, and the classes build mode never draws.
        ///
        /// It prints so the numbers can be read, and it asserts so they cannot quietly rot.
        /// </summary>
        [Test]
        public void EveryObjectMeasuredAndPrinted()
        {
            var high = new P3(0f, 1.5f, -0.75f);
            var cases = new[]
            {
                new Case { Name = "bottle",          ClassName = "bottle",     Body = Bottle(0.0f), Eye = Eye, ToleranceM = 0.03f },
                new Case { Name = "bottle, further", ClassName = "bottle",     Body = Bottle(0.6f), Eye = Eye, ToleranceM = 0.03f },
                new Case { Name = "bottle, far",     ClassName = "bottle",     Body = Bottle(1.6f), Eye = Eye, ToleranceM = 0.03f },
                new Case { Name = "bottle, 4 m",     ClassName = "bottle",     Body = Bottle(3.0f), Eye = Eye, Options = new DepthScene.Options { WallZ = 6f }, ToleranceM = 0.04f },
                new Case { Name = "bottle, noisy",   ClassName = "bottle",     Body = Bottle(0.0f), Eye = Eye, Options = new DepthScene.Options { NoiseM = 0.012f }, ToleranceM = 0.04f },
                new Case { Name = "cup",             ClassName = "cup",        Body = new DepthScene.Body { Centre = new P3(0.2f, 0.79f, 0.2f), Size = new P3(0.085f, 0.1f, 0.085f), Cylinder = true }, Eye = Eye, ToleranceM = 0.03f },
                new Case { Name = "laptop",          ClassName = "laptop",     Body = new DepthScene.Body { Centre = new P3(0f, 0.75f, 0.25f), Size = new P3(0.32f, 0.02f, 0.22f) }, Eye = high, ToleranceM = 0.04f },
                // A phone lying flat is 8 mm proud of the desk — inside the depth noise that the support slab has to
                // clear. It cannot be told apart from the table it lies on, and saying so beats both of the
                // alternatives: a confident little box built from noisy crumbs, or a table-sized one.
                new Case { Name = "phone, flat",     ClassName = "cell phone", Body = new DepthScene.Body { Centre = new P3(-0.2f, 0.744f, 0.2f), Size = new P3(0.15f, 0.008f, 0.07f) }, Eye = new P3(0f, 1.45f, -0.65f), ToleranceM = null },
                new Case { Name = "scissors",        ClassName = "scissors",   Body = new DepthScene.Body { Centre = new P3(0.1f, 0.745f, 0.2f), Size = new P3(0.2f, 0.01f, 0.07f) }, Eye = high, ToleranceM = 0.03f },
                new Case { Name = "book turned 30",  ClassName = "book",       Body = new DepthScene.Body { Centre = new P3(0f, 0.76f, 0.25f), Size = new P3(0.35f, 0.04f, 0.35f), YawDeg = 30f }, Eye = high, ToleranceM = 0.06f },
                new Case { Name = "book turned -15", ClassName = "book",       Body = new DepthScene.Body { Centre = new P3(0f, 0.76f, 0.25f), Size = new P3(0.3f, 0.04f, 0.2f), YawDeg = -15f }, Eye = high, ToleranceM = 0.05f },
                // Head-on, a bag's depth does not exist in the data: one viewpoint sees one face. Rejecting is the
                // honest answer, and the user walking a step to the side is the fix — the next case is that step.
                new Case { Name = "backpack, head-on", ClassName = "backpack", Body = new DepthScene.Body { Centre = new P3(0f, 0.96f, 0.5f), Size = new P3(0.3f, 0.44f, 0.2f) }, Eye = Eye, ToleranceM = null },
                new Case { Name = "backpack, corner",  ClassName = "backpack", Body = new DepthScene.Body { Centre = new P3(0f, 0.96f, 0.5f), Size = new P3(0.3f, 0.44f, 0.2f) }, Eye = new P3(-0.95f, 1.35f, -0.55f), ToleranceM = 0.08f },
                new Case { Name = "bottle, clear",   ClassName = "bottle",     Body = new DepthScene.Body { Centre = Bottle().Centre, Size = Bottle().Size, Cylinder = true, Invisible = true }, Eye = Eye, ToleranceM = null },
                new Case { Name = "person",          ClassName = "person",     Body = new DepthScene.Body { Centre = new P3(0f, 0.9f, 1.5f), Size = new P3(0.5f, 1.7f, 0.3f) }, Eye = Eye, ToleranceM = null },
                new Case { Name = "chair",           ClassName = "chair",      Body = new DepthScene.Body { Centre = new P3(0.6f, 0.45f, 1.0f), Size = new P3(0.5f, 0.9f, 0.5f) }, Eye = Eye, ToleranceM = null },
            };

            var problems = new List<string>();
            Console.WriteLine();
            Console.WriteLine("  object           reach  truth (cm)       measured (cm)    size err  centre err   yaw  conf  pts   verdict");
            Console.WriteLine("  " + new string('-', 118));
            foreach (var c in cases)
            {
                var reach = (c.Body.Centre - c.Eye).Length;
                var truth = $"{c.Body.Size.X * 100:0.#}x{c.Body.Size.Y * 100:0.#}x{c.Body.Size.Z * 100:0.#}";
                var mustReject = !c.ToleranceM.HasValue;

                // Build mode never boxes people or furniture; the class is refused before any depth is touched.
                if (SizePriors.Ignored(c.ClassName))
                {
                    var ok = mustReject;
                    if (!ok) problems.Add($"{c.Name}: the class is on the never-box list but the case expects a box");
                    Console.WriteLine($"  {c.Name,-16} {reach,5:0.0}  {truth,-16} {"not boxed",-16} {"",8} {"",10} {"",5} {"",5} {"",4}   {(ok ? "ok" : "WRONG")}");
                    continue;
                }

                var fit = FitOf(c.Body, c.ClassName, c.Eye, c.Options);
                if (!fit.Ok)
                {
                    var ok = mustReject;
                    if (!ok) problems.Add($"{c.Name}: {fit.Reason}");
                    Console.WriteLine($"  {c.Name,-16} {reach,5:0.0}  {truth,-16} {"rejected",-16} {"",8} {"",10} {"",5} {"",5} {fit.Points,4}   {(ok ? "ok" : "WRONG")}  {fit.Reason}");
                    continue;
                }

                var measured = $"{fit.Size.X * 100:0.#}x{fit.Size.Y * 100:0.#}x{fit.Size.Z * 100:0.#}";
                var sizeErr = Math.Max(Math.Abs(fit.Size.X - c.Body.Size.X),
                              Math.Max(Math.Abs(fit.Size.Y - c.Body.Size.Y), Math.Abs(fit.Size.Z - c.Body.Size.Z))) * 100f;
                var centreErr = (fit.Centre - c.Body.Centre).Length * 100f;
                var yawErr = Math.Abs(BoxMath.DeltaYaw(c.Body.YawDeg, fit.YawDeg));

                var verdict = "ok";
                if (mustReject)
                {
                    verdict = "WRONG";
                    problems.Add($"{c.Name}: measured {measured} cm when nothing should have been measured");
                }
                else
                {
                    var tolerance = c.ToleranceM.Value * 100f;
                    if (sizeErr > tolerance) { verdict = "WRONG"; problems.Add($"{c.Name}: size out by {sizeErr:0.#} cm (allowed {tolerance:0.#})"); }
                    else if (centreErr > tolerance) { verdict = "WRONG"; problems.Add($"{c.Name}: centre out by {centreErr:0.#} cm (allowed {tolerance:0.#})"); }
                    else if (!c.Body.Cylinder && yawErr > 12f) { verdict = "WRONG"; problems.Add($"{c.Name}: yaw out by {yawErr:0.#}°"); }
                }
                Console.WriteLine($"  {c.Name,-16} {reach,5:0.0}  {truth,-16} {measured,-16} {sizeErr,8:0.#} {centreErr,10:0.#} {fit.YawDeg,5:0.#} {fit.Confidence,5:0.00} {fit.Points,4}   {verdict}");
            }
            Console.WriteLine();

            if (problems.Count > 0) Assert.Fail(string.Join("\n", problems));
        }
    }
}
