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
    /// Build mode and the server's session over a venue's Wi-Fi: uploads that take seconds, answers that arrive after the
    /// headset has moved on, and a server that has restarted or been given a new session from the Director page.
    /// </summary>
    public class BuildModeSessionTests
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

        static Task<HttpResult> Accepted(string scan, string session) => StubServer.Json(202, "{\"scan_id\":\"" + scan + "\",\"session_id\":\"" + session + "\"}");

        [UnityTest]
        public IEnumerator OneScanAtATimeAndEachIsSentWithTheSessionAsItIsWhenItLeaves()
        {
            var app = StartApp("[App] (build session test)", copilot: true);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            TableSurface surface = null; StubServer server = null;
            yield return ScanRig(mode, (s, srv) => { surface = s; server = srv; }, raysPerFrame: 8);   // 16 × 12 rays, 8 a frame: a scan that takes 24 frames
            var first = new TaskCompletionSource<HttpResult>();
            server.Answer = _ => first.Task;                                   // the upload takes its time, as it does over venue Wi-Fi

            Call(mode, "StartScan");
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Scanning));
            yield return Until(() => server.To("/v1/build/scans").Count == 1, 10f, "the scan was never uploaded");
            Assert.That(surface.Rays, Is.EqualTo(16 * 12), "every cell's ray was cast");
            Assert.That(server.Requests[0].Body, Does.Not.Contain("session_id"), "the first scan has no session yet");

            Press(mode, ButtonGesture.Press);                                  // X again before the first scan's 202
            for (int i = 0; i < 30; i++) yield return null;
            Assert.That(new[] { surface.Rays, server.To("/v1/build/scans").Count }, Is.EqualTo(new[] { 16 * 12, 1 }),
                "a second scan went out before the first was answered: with no session, the server opens a second one");

            first.SetResult(Accepted("scan_1", "bsess_1").Result);
            yield return Until(() => !Flow(mode).ScanInFlight, 5f, "the 202 never landed");

            // Another view. Its session is read when it is SENT: a Director replay lands while its rays are still being cast.
            server.Answer = _ => Accepted("scan_2", "bsess_replay");
            Press(mode, ButtonGesture.Press);
            yield return null; yield return null;
            Assert.That(server.To("/v1/build/scans").Count, Is.EqualTo(1), "still casting rays");
            Send(mode, Inventory(new[] { 0.1, 0.8185, 0.5 }, labelled: false, session: "bsess_replay"));
            yield return Until(() => server.To("/v1/build/scans").Count == 2, 10f, "the second scan was never uploaded");
            Assert.That(server.To("/v1/build/scans")[1].Body, Does.Contain("\"session_id\":\"bsess_replay\""), "sent with the session as it was when the scan started");
            Object.Destroy(app.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator LeavingStopsTheScanAndALateAnswerDoesNotSwitchBuildModeBackOn()
        {
            var app = StartApp("[App] (build leave-mid-scan test)", copilot: true);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            TableSurface surface = null; StubServer server = null;
            yield return ScanRig(mode, (s, srv) => { surface = s; server = srv; }, raysPerFrame: 8);

            // Leave while the rays are still being cast (the Director cleared the build, or X was held): nothing is uploaded afterwards.
            Call(mode, "StartScan");
            yield return null; yield return null;
            Assert.That(surface.Rays, Is.InRange(1, 16 * 12 - 1), "the scan is under way");
            Press(mode, ButtonGesture.Hold);
            int castWhenLeft = surface.Rays;
            for (int i = 0; i < 40; i++) yield return null;
            Assert.That(new object[] { Phase(mode), surface.Rays, server.Requests.Count }, Is.EqualTo(new object[] { BuildPhase.Off, castWhenLeft, 0 }),
                "the scan went on after build mode was left, and uploaded");

            // X works again at once. This time leave while the upload is in flight; its 202 and its objects arrive afterwards.
            var late = new TaskCompletionSource<HttpResult>();
            server.Answer = _ => late.Task;
            Press(mode, ButtonGesture.Press);
            yield return Until(() => server.To("/v1/build/scans").Count == 1, 10f, "a scan after leaving never started: the stopped one left the capture busy");
            Press(mode, ButtonGesture.Hold);
            late.SetResult(Accepted("scan_late", "bsess_late").Result);
            for (int i = 0; i < 5; i++) yield return null;
            Send(mode, Inventory(new[] { 0.1, 0.8185, 0.5 }, labelled: true, session: "bsess_late"));
            yield return null;
            Assert.That(new object[] { Phase(mode), ModeOf(app), Hologram().gameObject.activeSelf }, Is.EqualTo(new object[] { BuildPhase.Off, "overlay", true }),
                "a scan that landed after build mode was left switched it back on, over the run that had taken its place");
            Object.Destroy(app.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AnIdeaTheServerNoLongerHasClearsThePreviewsAndSaysToScanAgain()
        {
            var app = StartApp("[App] (build gone-idea test)", copilot: false);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            var input = new FakeInput(); Set(mode, "_input", input);
            var server = UseStubServer(mode);                                  // answers 404: it restarted, or the Director page opened a new session
            Send(mode, Inventory(new[] { 0.1, 0.8185, 0.5 }));
            Send(mode, IdeasFixture());
            yield return new WaitForFixedUpdate();
            yield return null;

            input.Pointer = new Ray(GameObject.Find("[Idea hit] Can on a stage").GetComponent<BoxCollider>().bounds.center + Vector3.back, Vector3.forward);
            yield return PullTrigger(input);
            Assert.That(server.To("/v1/build/ideas/idea_fixture/start").Count, Is.EqualTo(1));
            yield return Until(() => Phase(mode) != BuildPhase.Starting, 5f, "the 404 never landed");
            yield return null;

            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Labelled), "previews that can never start were left up");
            Assert.That(GameObject.Find("[Idea] Can on a stage"), Is.Null);
            Assert.That(Toast(), Is.EqualTo("Those ideas are gone. Scan again (X)."));
            Assert.That(GameObject.Find("[BuildTwins]").transform.Find("part_o1"), Is.Not.Null, "the objects are still shown");
            Assert.That(Flow(mode).CanScanFromButton, Is.True);
            Object.Destroy(app.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AReplayFromTheDirectorPageTakesDownThePreviewsOfTheSessionBefore()
        {
            var app = StartApp("[App] (build replay test)", copilot: false);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            Send(mode, Inventory(new[] { 0.1, 0.8185, 0.5 }));
            Send(mode, IdeasFixture());
            yield return null;
            Assert.That(GameObject.Find("[Idea] Can on a stage"), Is.Not.Null);

            Send(mode, Inventory(new[] { 0.3, 0.8185, 0.6 }, labelled: true, session: "bsess_replay"));
            yield return null;
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Labelled));
            Assert.That(GameObject.Find("[Idea] Can on a stage"), Is.Null, "the old session's previews stayed up: every one of them would answer 404");
            Object.Destroy(app.gameObject);
            yield return null;
        }
    }
}
