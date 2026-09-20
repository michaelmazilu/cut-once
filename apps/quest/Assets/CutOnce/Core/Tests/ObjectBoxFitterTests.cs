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
            Assert.That(fit.Reason, Does.Contain("does not match bottle"));
        }

        [Test]
        public void PeopleAndFurnitureAreNeverBoxed()
        {
            Assert.That(SizePriors.Ignored("person"), Is.True);
            foreach (var room in new[] { "chair", "diningtable", "tvmonitor", "sofa", "refrigerator", "pottedplant" })
                Assert.That(SizePriors.Ignored(room), Is.True, room);
            foreach (var material in new[] { "bottle", "laptop", "cup", "book" })
                Assert.That(SizePriors.Ignored(material), Is.False, material);
        }

        [Test]
        public void NamesMatchTheModel()
        {
            // The list above is only worth anything if it spells classes the way the MODEL does. It did not: the
            // detector emits darknet's COCO names, so `couch`, `dining table`, `tv` and `potted plant` matched
            // nothing and four furniture classes walked straight through the filter meant to stop them. Asserting
            // against hardcoded strings could never have caught that, so this reads the model's own label file.
            var labels = System.IO.File.ReadAllLines(System.IO.Path.Combine(
                RepoFiles.Root, "apps", "quest", "Assets", "CutOnce", "Vision", "Resources", "SentisYoloClasses.txt"));
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in labels)
                if (!string.IsNullOrWhiteSpace(line)) known.Add(line.Trim());

            Assert.That(known, Is.Not.Empty, "the label file should not be empty");
            foreach (var name in SizePriors.AllKnownNames())
                Assert.That(known.Contains(name), Is.True,
                    $"'{name}' is not a class this model can ever emit — check the spelling against SentisYoloClasses.txt");
        }

        [Test]
        public void ASliverIsNeverInflatedIntoACylinder()
        {
            // A flat face — a patch of wall, the side of a carton, a bad cluster — measured 12 x 10 x 0.5 cm. The
            // round-class depth completion used to widen that sliver to 12 cm and hand back a confident "cup",
            // manufacturing the very dimension the plausibility check exists to police.
            var body = new DepthScene.Body { Centre = new P3(0f, 0.85f, 1.2f), Size = new P3(0.12f, 0.1f, 0.005f) };
            foreach (var round in new[] { "cup", "bottle", "vase" })
            {
                var fit = FitOf(body, round, Eye);
                Assert.That(fit.Ok, Is.False, $"a 5 mm sliver must not become a {round}: {fit}");
            }
        }

        [Test]
        public void TheFloorOnlyEverMovesDownToTheSurface()
        {
            // MRUK's plane is not always where the surface is — a tablecloth, a mat, or plain anchor drift puts it
            // a few centimetres out, and then it cuts THROUGH what is standing there. Snapping a box's floor UP to
            // such a plane crops the box; the snap exists to extend a box DOWN onto a surface, never to cut one off
            // at it. Left unguarded this took 30 cm off the panel below at full confidence.
            //
            // The cloud is built by hand rather than ray-cast: the scene's table plane is opaque and infinite, so
            // nothing below a surface is ever visible in it, and this case needs points on both sides of one.
            var eye = new P3(0f, 1.0f, -0.9f);
            var panel = new List<P3>();
            for (var y = 0.50f; y <= 0.99f; y += 0.02f)
            {
                if (Math.Abs(y - 0.80f) <= 0.013f) continue;         // the slab the support removal will take out
                for (var x = -0.15f; x <= 0.15f; x += 0.02f) panel.Add(new P3(x, y, 3.0f));      // the face…
                for (var z = 3.02f; z <= 3.12f; z += 0.02f) panel.Add(new P3(-0.15f, y, z));     // …and one side
            }

            var fit = ObjectBoxFitter.Fit(panel, eye, SizePriors.For("teddy bear"), new FitOptions { SupportY = 0.80f });
            Assert.That(fit.Ok, Is.True, fit.Reason);
            Assert.That(fit.Size.Y, Is.GreaterThan(0.45f), $"the box was cropped at the plane: {fit}");
            Assert.That(fit.Centre.Y, Is.EqualTo(0.745f).Within(0.03f), $"and its centre rose with it: {fit}");
        }

        [Test]
        public void AYawFromNowhereDoesNotHang()
        {
            // Wrap90 used to loop subtracting 90 until the value came down, which never happens for a large float.
            // It is called on every smoother update, so a single garbage yaw would wedge the render thread.
            Assert.That(BoxMath.Wrap90(1e10f), Is.InRange(-45f, 45f));
            Assert.That(BoxMath.Wrap90(float.PositiveInfinity), Is.EqualTo(0f));
            Assert.That(BoxMath.Wrap90(float.NaN), Is.EqualTo(0f));
            Assert.That(BoxMath.Wrap90(-1e9f), Is.InRange(-45f, 45f));
        }

        [Test]
        public void ANonsensePoseIsRefusedAtTheDoor()
        {
            var patch = DepthScene.Patch(Bottle(), Eye);
            var fit = ObjectBoxFitter.Fit(patch, new P3(float.NaN, 1.35f, -0.9f), SizePriors.For("bottle"));
            Assert.That(fit.Ok, Is.False);
            Assert.That(fit.Reason, Does.Contain("not a number"), fit.Reason);
        }

        [Test]
        public void ADisagreeingFitBarelyMovesASettledBox()
        {
            var smoother = new BoxSmoother();
            var body = Bottle();
            for (var i = 0; i < 4; i++) smoother.Update(FitOf(body, "bottle", Eye, new DepthScene.Options { Seed = i }), 1f / 3f);
            var settled = smoother.Size;

            // Same place, wildly different size, over and over. It should creep, not leap.
            for (var i = 0; i < 4; i++)
                smoother.Update(FitResult.Fitted(smoother.Centre, 0f, new P3(0.4f, 0.9f, 0.4f), 1f, 200), 1f / 3f);
            Assert.That(smoother.Size.Y - settled.Y, Is.LessThan(0.1f), $"held size ran away to {smoother.Size}");
            Assert.That(smoother.Stable, Is.False, "fits that disagree must not count towards stability");
        }

        [Test]
        public void NoTimePassingConfirmsNothing()
        {
            var smoother = new BoxSmoother();
            var body = Bottle();
            for (var i = 0; i < 4; i++) smoother.Update(FitOf(body, "bottle", Eye, new DepthScene.Options { Seed = i }), 0f);
            Assert.That(smoother.HasBox, Is.True, "the first sighting still gives a box");
            Assert.That(smoother.Stable, Is.False, "three zero-length cycles are not three confirmations");
        }

        [Test]
        public void JumpsEitherSideOfAGapAreNotOneStory()
        {
            var smoother = new BoxSmoother();
            var body = Bottle();
            for (var i = 0; i < 4; i++) smoother.Update(FitOf(body, "bottle", Eye, new DepthScene.Options { Seed = i }), 1f / 3f);
            var held = smoother.Centre;

            var faraway = FitResult.Fitted(new P3(0f, 1.0f, 2.9f), 0f, new P3(0.07f, 0.25f, 0.07f), 0.9f, 80);
            smoother.Update(faraway, 1f / 3f);                       // one jump…
            for (var i = 0; i < 20; i++) smoother.Miss(1f / 3f);     // …then seven seconds of seeing nothing
            smoother.Update(faraway, 1f / 3f);
            smoother.Update(faraway, 1f / 3f);
            Assert.That((smoother.Centre - held).Length, Is.LessThan(0.1f),
                "jumps minutes apart were adopted as though they were consecutive");
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
        /// <summary>
        /// The old sizing and the new one on IDENTICAL depth, so the claim that this is better is a measurement and
        /// not a hope. Both read the same scene through the same sensor; only the maths between differs.
        ///
        /// Error is the worst of the three axes, in centimetres, against the truth. A pipeline that refuses to
        /// answer scores no error — refusing is a legitimate outcome, and the columns say which happened.
        /// </summary>
        [Test]
        public void TheOldWayAndTheNewWaySideBySide()
        {
            var high = new P3(0f, 1.5f, -0.75f);
            var cases = new (string name, string className, DepthScene.Body body, P3 eye, DepthScene.Options options)[]
            {
                ("bottle",            "bottle",     Bottle(0.0f), Eye,  null),
                ("bottle, far",       "bottle",     Bottle(1.6f), Eye,  null),
                ("bottle, wide box",  "bottle",     Bottle(0.0f), Eye,  new DepthScene.Options { Margin = 0.8f }),
                ("cup",               "cup",        new DepthScene.Body { Centre = new P3(0.2f, 0.79f, 0.2f), Size = new P3(0.085f, 0.1f, 0.085f), Cylinder = true }, Eye, null),
                ("laptop",            "laptop",     new DepthScene.Body { Centre = new P3(0f, 0.75f, 0.25f), Size = new P3(0.32f, 0.02f, 0.22f) }, high, null),
                ("scissors",          "scissors",   new DepthScene.Body { Centre = new P3(0.1f, 0.745f, 0.2f), Size = new P3(0.2f, 0.01f, 0.07f) }, high, null),
                ("book turned 30",    "book",       new DepthScene.Body { Centre = new P3(0f, 0.76f, 0.25f), Size = new P3(0.35f, 0.04f, 0.35f), YawDeg = 30f }, high, null),
                ("backpack, corner",  "backpack",   new DepthScene.Body { Centre = new P3(0f, 0.96f, 0.5f), Size = new P3(0.3f, 0.44f, 0.2f) }, new P3(-0.95f, 1.35f, -0.55f), null),
                ("bottle, clear",     "bottle",     new DepthScene.Body { Centre = Bottle().Centre, Size = Bottle().Size, Cylinder = true, Invisible = true }, Eye, null),
                ("person",            "person",     new DepthScene.Body { Centre = new P3(0f, 0.9f, 1.5f), Size = new P3(0.5f, 1.7f, 0.3f) }, Eye, null),
                ("chair",             "chair",      new DepthScene.Body { Centre = new P3(0.6f, 0.45f, 1.0f), Size = new P3(0.5f, 0.9f, 0.5f) }, Eye, null),
            };

            double oldTotal = 0, newTotal = 0;
            int oldMeasured = 0, newMeasured = 0, newWins = 0, oldWins = 0;

            Console.WriteLine();
            Console.WriteLine("                      |            OLD (ships today)           |             NEW             |");
            Console.WriteLine("  object              | measured (cm)     size err  centre err | measured (cm)   err  centre |");
            Console.WriteLine("  " + new string('-', 106));
            foreach (var c in cases)
            {
                var truth = c.body.Size;
                string oldCell, newCell;
                double oldErr = -1, newErr = -1;

                if (LegacyLocator.TryLocate(c.body, c.eye, c.className, c.options, out var oldWorld, out var oldSize, out var oldWhy))
                {
                    oldErr = Worst(oldSize, truth);
                    var oldCentre = (oldWorld - c.body.Centre).Length * 100f;
                    oldCell = $"{Show(oldSize),-17} {oldErr,8:0.#} {oldCentre,10:0.#}";
                    oldTotal += oldErr; oldMeasured++;
                }
                else oldCell = $"{"refused",-17} {oldWhy,19}";

                if (SizePriors.Ignored(c.className))
                {
                    newCell = $"{"never boxed",-15} {"",5} {"",6}";
                }
                else
                {
                    var fit = FitOf(c.body, c.className, c.eye, c.options);
                    if (fit.Ok)
                    {
                        newErr = Worst(fit.Size, truth);
                        var newCentre = (fit.Centre - c.body.Centre).Length * 100f;
                        newCell = $"{Show(fit.Size),-15} {newErr,5:0.#} {newCentre,6:0.#}";
                        newTotal += newErr; newMeasured++;
                    }
                    else newCell = $"{"refused",-15} {"",5} {"",6}";
                }

                if (oldErr >= 0 && newErr >= 0) { if (newErr < oldErr) newWins++; else oldWins++; }
                Console.WriteLine($"  {c.name,-19} | {oldCell} | {newCell} |");
            }

            Console.WriteLine();
            Console.WriteLine($"  worst-axis error, averaged over what each MEASURED:  old {oldTotal / Math.Max(1, oldMeasured):0.0} cm over {oldMeasured} cases,  new {newTotal / Math.Max(1, newMeasured):0.0} cm over {newMeasured}");
            Console.WriteLine($"  head to head where BOTH measured:  new closer in {newWins}, old closer in {oldWins}");
            Console.WriteLine();

            Assert.That(newWins, Is.GreaterThan(oldWins), "the rebuild has to actually win on the same data");
        }

        static float Worst(P3 measured, P3 truth) =>
            Math.Max(Math.Abs(measured.X - truth.X), Math.Max(Math.Abs(measured.Y - truth.Y), Math.Abs(measured.Z - truth.Z))) * 100f;

        static string Show(P3 size) => $"{size.X * 100:0.#}x{size.Y * 100:0.#}x{size.Z * 100:0.#}";

        /// <summary>How long one fit takes over a `pixels` x `pixels` patch, averaged, with the JIT already warm.</summary>
        static double FitMs(int pixels, int rounds)
        {
            var patches = new List<List<P3>>();
            foreach (var z in new[] { 0.0f, 0.6f, 1.6f })
                patches.Add(DepthScene.Patch(Bottle(z), Eye, new DepthScene.Options { Pixels = pixels }));

            var prior = SizePriors.For("bottle");
            foreach (var patch in patches) ObjectBoxFitter.Fit(patch, Eye, prior);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < rounds; i++)
                foreach (var patch in patches)
                    ObjectBoxFitter.Fit(patch, Eye, prior);
            clock.Stop();
            return clock.Elapsed.TotalMilliseconds / (rounds * patches.Count);
        }

        [Test]
        public void WhatAFitCosts()
        {
            // This REPORTS; it barely guards, and the comment says so rather than implying otherwise.
            //
            // It began as `Assert.That(each, Is.LessThan(5.0))`, which failed at 22.8 ms on a laptop running a Unity
            // import at load average 48 — having measured 1.1 ms on the same commit minutes earlier. Wall-clock on a
            // shared machine is not a property of the code.
            //
            // The obvious repair was to assert on SCALING instead, four times the points costing four times the
            // time. That was measured too, by making the spacing probe exhaustive on purpose: the ratio went from
            // 3.0x to 5.0x. Real, but far too narrow to separate reliably under load — and the reason is worth
            // keeping: the spacing probe is only one part of a fit, and the linear clustering around it dilutes
            // anything quadratic inside it. A threshold splitting 3.0 from 5.0 would be fitted to one evening's
            // noise and would fail people later for no reason.
            //
            // So: print both numbers for a human, and fail only on a catastrophe no amount of load explains.
            var small = FitMs(16, 120);      // 256 points
            var large = FitMs(32, 120);      // 1024 points — four times as many
            Console.WriteLine($"\n  one fit: {small:0.000} ms over 16x16, {large:0.000} ms over 32x32 " +
                              $"— x{large / Math.Max(small, 1e-6):0.0} for 4x the points (idle: about 1 ms at 32x32)\n");

            Assert.That(large, Is.LessThan(250.0), $"a single fit took {large:0.0} ms, which no amount of load explains");
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
