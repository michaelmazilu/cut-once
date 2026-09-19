using System.Collections;
using System.Collections.Generic;
using CutOnce.Core;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CutOnce.Device.PlayTests.BuildModeHarness;

namespace CutOnce.Device.PlayTests
{
    /// <summary>
    /// Labels and ideas take up to half a minute, and the stream only resends the current run when it reconnects. If the
    /// Wi-Fi drops in that window the headset would wait for ever with the run hidden, so on a reconnect it asks the server
    /// for the session and takes its objects and ideas through the same two doors the stream uses.
    /// </summary>
    public class BuildModeReconnectTests
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

        /// <summary>GET /v1/build/sessions/current as the server answers it: the session, and the named objects and ideas it holds.</summary>
        static string Snapshot(string session) =>
            "{\"session\":{\"session_id\":\"" + session + "\",\"created_at\":\"2026-09-19T12:00:00.000Z\",\"scans\":[\"scan_1\"]},\"surfaces\":[],\"twins\":" +
            CoreJson.Write(new List<TwinDto> { Can(new[] { 0.1, 0.8185, 0.5 }) }) + ",\"ideas\":" + CoreJson.Write(IdeasFixture().ideas) + "}";

        /// <summary>What CutOnceApp does when the stream's connection count changes: make it look as if one was just made.</summary>
        static void Reconnect(Component app) => AppType.GetField("_seenConnects", Hidden).SetValue(app, -1);

        [UnityTest]
        public IEnumerator AfterAReconnectTheHeadsetAsksForTheSessionAndShowsWhatItMissed()
        {
            var app = StartApp("[App] (build reconnect test)", copilot: false);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            var server = UseStubServer(mode);
            server.Answer = r => StubServer.Json(200, Snapshot("bsess_fixture"));
            Send(mode, Inventory(new[] { 0.1, 0.8185, 0.5 }, labelled: false));   // outlines, still unnamed... and then the Wi-Fi drops
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Scanning));

            Reconnect(app);
            yield return Until(() => Phase(mode) == BuildPhase.Ideas, 5f, "the labels and ideas the stream dropped were never fetched: the headset waits for ever with the run hidden");
            yield return null;
            Assert.That(server.To("/v1/build/sessions/current").Count, Is.EqualTo(1));
            Assert.That(GameObject.Find("[BuildTwins]").transform.Find("part_o1"), Is.Not.Null);
            Assert.That(GameObject.Find("[Idea] Can on a stage"), Is.Not.Null);
            Object.Destroy(app.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator AnotherSessionsSnapshotIsIgnoredAndNothingIsAskedWithBuildModeOff()
        {
            var app = StartApp("[App] (build reconnect, other session)", copilot: false);
            yield return UntilTheFirstRunShows();
            var mode = BuildModeOf();
            var server = UseStubServer(mode);
            server.Answer = r => StubServer.Json(200, Snapshot("bsess_someone_elses"));

            Reconnect(app);                                                     // build mode is off: the app's own catch-up, nothing of ours
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(server.Requests, Is.Empty);

            Send(mode, Inventory(new[] { 0.1, 0.8185, 0.5 }, labelled: false));
            Reconnect(app);
            yield return Until(() => server.To("/v1/build/sessions/current").Count == 1, 5f, "the session was never asked for");
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(Phase(mode), Is.EqualTo(BuildPhase.Scanning), "the Director page moved on to another session: its objects are not this scan's");
            Assert.That(GameObject.Find("[Idea] Can on a stage"), Is.Null);
            Object.Destroy(app.gameObject);
            yield return null;
        }
    }
}
