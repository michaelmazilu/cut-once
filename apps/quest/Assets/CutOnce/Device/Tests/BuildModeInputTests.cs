using System.Collections;
using System.Threading.Tasks;
using CutOnce.Core;
using CutOnce.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CutOnce.Device.PlayTests.BuildModeHarness;

namespace CutOnce.Device.PlayTests
{
    /// <summary>
    /// Judges wear the headset holding both controllers, so every button will be pressed by accident sooner or later.
    /// These tests hold build mode's inputs to what they mean: X scans only until a design is chosen, holding X leaves,
    /// the trigger picks the preview it is in or on and otherwise leaves the previews alone, and nothing scans mid-flight.
    /// </summary>
    public class BuildModeInputTests
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

        [UnityTest]
        public IEnumerator XIsIgnoredWhileADesignIsBeingBuiltAndHoldingItLeavesBuildMode()
        {
            var app = StartApp("[App] (build button test)", copilot: false);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            yield return ReachTheWalkthrough(app, mode);

            Press(mode, ButtonGesture.Press);                                  // a thumb on X while placing the can
            yield return null;
            Assert.That(new object[] { Phase(mode), Hologram().gameObject.activeSelf }, Is.EqualTo(new object[] { BuildPhase.Walkthrough, true }),
                "the walkthrough and its design are still there");

            Press(mode, ButtonGesture.Hold);                                   // held for a second: leave
            yield return null;
            Assert.That(new object[] { Phase(mode), ModeOf(app), Hologram().gameObject.activeSelf }, Is.EqualTo(new object[] { BuildPhase.Off, "overlay", true }));
            Assert.That(Toast(), Is.EqualTo("Left build mode"));
            Assert.That(Hologram().Plan.plan_id, Is.EqualTo("plan_build_fixture"), "the design stays as an ordinary run");

            Press(mode, ButtonGesture.Hold);                                   // nothing to leave: nothing happens
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Off));
            Object.Destroy(app.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator TheTriggerPicksThePreviewItIsInOrOnAndOtherwiseLeavesThePreviewsAlone()
        {
            var app = StartApp("[App] (build trigger test)", copilot: false);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            var input = new FakeInput(); Set(mode, "_input", input);
            var server = UseStubServer(mode);
            var starting = new TaskCompletionSource<HttpResult>();
            server.Answer = _ => starting.Task;                                // the server takes its time, so Starting can be seen
            Send(mode, Inventory(new[] { 0.1, 0.8185, 0.5 }));
            Send(mode, IdeasFixture());
            yield return new WaitForFixedUpdate();                             // the preview's collider is in the physics scene
            yield return null;
            var preview = GameObject.Find("[Idea hit] Can on a stage").GetComponent<BoxCollider>();

            // A trigger that hits nothing: the previews stay (it used to rescan, which cleared them).
            input.Pointer = new Ray(preview.bounds.center + Vector3.back, Vector3.up);
            yield return PullTrigger(input);
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Ideas));
            Assert.That(preview != null, Is.True, "the very same previews are still there to pick from, not cleared and rebuilt by a rescan");
            Assert.That(Toast(), Does.Not.Contain("Scanning"), "no scan was started");   // build mode's own scan message; the app's start-up hint mentions X for scanning
            Assert.That(server.Requests, Is.Empty);

            // Reaching into the preview and pulling the trigger: physics never reports a collider the ray starts inside.
            input.Pointer = new Ray(preview.bounds.center, Vector3.forward);
            yield return PullTrigger(input);
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Starting), "a hand inside the preview picks it");
            Assert.That(server.To("/v1/build/ideas/idea_fixture/start").Count, Is.EqualTo(1));

            starting.SetResult(new HttpResult { Status = 500, Body = "{}" });   // the start fails: back to choosing
            yield return Until(() => Phase(mode) == BuildPhase.Ideas, 5f, "a failed start goes back to the ideas");

            // Pointing at it from a metre away still works, through physics.
            server.Answer = _ => new TaskCompletionSource<HttpResult>().Task;
            input.Pointer = new Ray(preview.bounds.center + Vector3.back, Vector3.forward);
            yield return PullTrigger(input);
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Starting));
            Object.Destroy(app.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AScanAskedForWhileThePiecesFlyIsIgnoredAndTheFlightStillEnds()
        {
            var app = StartApp("[App] (build mid-flight scan test)", copilot: false);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            Send(mode, Inventory(new[] { 0.6, 0.8185, 0.9 }));
            Send(mode, IdeasFixture());
            yield return null;
            LogAssert.ignoreFailingMessages = true;                            // the Editor cannot make the lock's spatial anchor (see the harness)
            LoadRun(app, IdeasFixture().ideas[0].plan, "asm_build_smoke");
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Assembling));

            Call(mode, "StartScan");                                           // "what can I build?", or X, while the pieces are in the air
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Assembling));
            yield return Until(() => Phase(mode) == BuildPhase.Walkthrough, 8f, "the flight was stopped and never ended: stuck in Assembling");
            yield return null;
            LogAssert.ignoreFailingMessages = false;
            Object.Destroy(app.gameObject);
            yield return null;
        }
    }
}
