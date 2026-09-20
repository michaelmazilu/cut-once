using System.Collections;
using CutOnce.AR;
using CutOnce.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CutOnce.Device.PlayTests.BuildModeHarness;

namespace CutOnce.Device.PlayTests
{
    /// <summary>
    /// Build mode inside the whole app, with no server and no headset: the messages the server would send are handed to
    /// it directly, and the run a picked design starts is loaded the way the app loads any run (BuildModeHarness). Like
    /// before, the app is pointed at a closed port and any config and journal on this machine are moved aside and
    /// put back.
    /// </summary>
    public class BuildModeTests
    {
        Isolation _isolation;

        [SetUp] public void SetUp() => _isolation = new Isolation();
        [TearDown] public void TearDown() => _isolation?.Restore();

        [UnityTearDown]
        public IEnumerator DestroyWhatTheAppCreated()
        {
            AppSmokeTests.DestroyAppObjects();
            yield return null;
        }

        // ── tests ───────────────────────────────────────────────────────────────────────────────────────────────
        [UnityTest]
        public IEnumerator BuildModeShowsTwinsAndIdeasItIsSentAndReportsBuildMode()
        {
            var app = StartApp("[App] (build smoke test)", copilot: false);
            yield return null;

            var mode = BuildModeOf();
            Assert.That(mode, Is.Not.Null, "the app did not attach BuildMode");
            Assert.That(ModeOf(app), Is.EqualTo("overlay"), "build mode is off until a scan or an inventory starts it");
            Send(mode, Inventory(new[] { 0.1, 0.8185, 0.5 }));
            Send(mode, IdeasFixture());
            yield return null;

            Assert.That(GameObject.Find("[BuildTwins]")?.transform.Find("part_o1"), Is.Not.Null, "the can's outline was not shown over the real can");
            Assert.That(GameObject.Find("[Idea] Can on a stage"), Is.Not.Null, "the idea preview was not shown");
            Assert.That(ModeOf(app), Is.EqualTo("build"));
            Assert.That(Hologram().gameObject.activeSelf, Is.False, "the run that was showing is out of the way while you choose");
            UnityEngine.Object.Destroy(app.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheChosenDesignLocksWhereTheServerSaysAndItsPiecesFlyInFromTheirObjects()
        {
            var app = StartApp("[App] (build flight test)", copilot: false);
            for (float waited = 0f; waited < 15f && (Hologram() == null || Hologram().Views.Count == 0); waited += Time.unscaledDeltaTime) yield return null;
            Assert.That(Hologram()?.Views.Count ?? 0, Is.GreaterThan(0), "the first run (the desk test plan, from the journal) never loaded");

            var mode = BuildModeOf();
            var ideas = IdeasFixture(); var idea = ideas.ideas[0];
            var canInRoom = new[] { 0.6, 0.8185, 0.9 };                          // the real can stands half a metre from where the design goes
            Send(mode, Inventory(canInRoom));
            Send(mode, ideas);
            yield return null;
            var twins = GameObject.Find("[BuildTwins]").transform;
            int highlightedObjectCount = twins.childCount;
            Assert.That(highlightedObjectCount, Is.GreaterThan(0), "the scanned objects were not highlighted before choosing a design");

            // Picking posts to the server, which starts a run of the idea's plan; the stream then makes the app load it.
            // Locking saves a spatial anchor, and anchors only exist on the headset: in the Editor Meta's OVRSpatialAnchor
            // logs an error when it cannot make one. That one log is expected, so errors are let through until the lock settles.
            LogAssert.ignoreFailingMessages = true;
            LoadRun(app, idea.plan, "asm_build_smoke");

            var hologram = Hologram();
            var alignment = hologram.GetComponent<AlignmentController>();
            Assert.That(new object[] { alignment.State, alignment.Method }, Is.EqualTo(new object[] { AlignmentState.Locked, "build" }));
            Assert.That(hologram.gameObject.activeSelf, Is.True, "the design is shown");
            // The fixture's origin is [0.1, 0.74, 0.5] with a right-handed yaw of +45° about +Y (quat [0, 0.3826834, 0, 0.9238795]):
            // in Unity x is mirrored, so (-0.1, 0.74, 0.5), and the design's front (+Z) turns to (sin -45°, 0, cos -45°).
            Assert.That(Vector3.Distance(hologram.transform.position, new Vector3(-0.1f, 0.74f, 0.5f)), Is.LessThan(1e-4f), $"locked where the server put it: {hologram.transform.position:F4}");
            Assert.That(Vector3.Distance(hologram.transform.forward, new Vector3(-0.70710678f, 0f, 0.70710678f)), Is.LessThan(1e-4f), $"turned as the server said: {hologram.transform.forward:F4}");
            Assert.That(Vector3.Distance(hologram.transform.up, Vector3.up), Is.LessThan(1e-4f), "and level");
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Assembling));
            var can = hologram.ViewOf("part_o1");
            Assert.That(Vector3.Distance(can.transform.position, new Vector3(-0.6f, 0.8185f, 0.9f)), Is.LessThan(1e-4f), $"the can's hologram starts on the real can, at (0.6, 0.8185, 0.9) in the plan's frame: {can.transform.position:F4}");
            var hud = GameObject.Find("[HUD]").transform;
            var stoodBehindTheDesign = hud.position;                            // stood by the lock, before the pieces left for their objects

            yield return null;
            for (float waited = 0f; waited < 5f && alignment.Hint.StartsWith("Saving"); waited += Time.unscaledDeltaTime) yield return null;
            yield return null;
            LogAssert.ignoreFailingMessages = false;
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Assembling), "the lock settles long before the 0.7 s flight ends");
            Assert.That(Vector3.Distance(hud.position, stoodBehindTheDesign), Is.LessThan(1e-4f),
                "the HUD was stood again while the can was still over at the real one, half a metre away: off-centre and high for the whole walkthrough");
            Assert.That(GameObject.Find("[Idea] Can on a stage"), Is.Null, "the previews are gone once one is picked");

            for (float waited = 0f; waited < 8f && Phase(mode) != BuildPhase.Walkthrough; waited += Time.unscaledDeltaTime) yield return null;
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Walkthrough), "the fly-together never finished");
            Assert.That(Vector3.Distance(can.transform.localPosition, new Vector3(0f, 0.0785f, 0f)), Is.LessThan(1e-5f), "and ends exactly in its place in the design (the plan's [0, 0.0785, 0])");
            Assert.That(Quaternion.Angle(can.transform.localRotation, Quaternion.identity), Is.LessThan(0.01f));
            yield return null;
            Assert.That(twins.childCount, Is.EqualTo(highlightedObjectCount), "choosing a design removed some of the scanned object highlights");
            Assert.That(ModeOf(app), Is.EqualTo("build"));

            // B with nothing pointed at: the whole step is done, and the walkthrough moves on.
            var store = StoreOf(app);
            Assert.That(store.Current.current_step_id, Is.EqualTo("step_01"));
            Assert.That((bool)Call(mode, "MarkCurrentStep"), Is.True);
            Assert.That(new[] { store.Current.parts["part_surface"].state, store.Current.current_step_id }, Is.EqualTo(new[] { "built", "step_02" }));

            // The Director starts another run: build mode steps aside for it, and it stands on the build site, where
            // the judge is looking. Its own origin is a corner, 30 cm and 20 cm from the middle of its footprint.
            var other = IdeasFixture().ideas[0].plan; other.plan_id = "plan_started_elsewhere";
            foreach (var part in other.parts) { part.position[0] += 0.3; part.position[2] += 0.2; }
            LogAssert.ignoreFailingMessages = true;                            // the Editor cannot make the lock's spatial anchor
            LoadRun(app, other, "asm_started_elsewhere");
            yield return null; yield return null;
            LogAssert.ignoreFailingMessages = false;
            Assert.That(new object[] { Phase(mode), ModeOf(app), Hologram().gameObject.activeSelf }, Is.EqualTo(new object[] { BuildPhase.Off, "overlay", true }));
            var footprint = new Bounds(); bool any = false;
            foreach (var view in Hologram().Views.Values) { if (!any) { footprint = view.WorldBounds; any = true; } else footprint.Encapsulate(view.WorldBounds); }
            // The fixture's site is [0.1, 0.74, 0.5] in the plan's frame: x is mirrored into Unity, so (-0.1, 0.74, 0.5).
            Assert.That(new Vector2(footprint.center.x - -0.1f, footprint.center.z - 0.5f).magnitude, Is.LessThan(2e-3f), "the new run's footprint is centred on the build site");
            Assert.That(footprint.min.y, Is.EqualTo(0.74f).Within(2e-3f), "and its lowest face rests on the table");
            Assert.That(new object[] { alignment.State, alignment.Method }, Is.EqualTo(new object[] { AlignmentState.Locked, "build" }));
            UnityEngine.Object.Destroy(app.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AScanThatFindsNoDepthLeavesBuildModeOffAndTheHologramShowing()
        {
            // The Editor has no depth sensor, so every one of the 128 × 96 rays misses and the scan fails with a reason. The
            // camera is a stand-in that always has a picture: with the copilot's stored photo (only there after pnpm
            // sync:fixtures) the scan would have failed before casting a ray, and this test would have proved nothing.
            var app = StartApp("[App] (build scan test)", copilot: true);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            CutOnce.Copilot.CopilotController copilot = null;
            yield return Until(() => (copilot = UnityEngine.Object.FindAnyObjectByType<CutOnce.Copilot.CopilotController>()) != null, 5f, "the app did not create the copilot");
            copilot.frameSourceBehaviour = copilot.gameObject.AddComponent<StubFrames>();
            LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex(@"\[CutOnce\] Scan: 0/12288 depth hits"));

            Call(mode, "StartScan");
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Scanning), "the scan did not start");
            Assert.That(Hologram().gameObject.activeSelf, Is.False, "the run that was showing is out of the way while the room is scanned");
            yield return Until(() => Phase(mode) == BuildPhase.Off, 15f, "a scan with no depth did not end with build mode off");

            Assert.That(new object[] { Phase(mode), ModeOf(app), Hologram().gameObject.activeSelf }, Is.EqualTo(new object[] { BuildPhase.Off, "overlay", true }));
            Assert.That(Toast(), Does.StartWith("Couldn't scan: no depth here yet"));
            UnityEngine.Object.Destroy(app.gameObject);
            yield return null;
        }
    }
}
