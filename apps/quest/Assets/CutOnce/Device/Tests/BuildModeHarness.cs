using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CutOnce.AR;
using CutOnce.Copilot;
using CutOnce.Core;
using CutOnce.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace CutOnce.Device.PlayTests
{
    /// <summary>
    /// What the build-mode PlayMode tests share: the whole app with no server and no headset, the messages the server
    /// would send, and stand-ins for the three things only a headset has (the controller, the depth sensor, the camera)
    /// plus a server that answers what a test tells it to. BuildMode lives in Assembly-CSharp (it names Meta types), so it
    /// is reached by name.
    /// </summary>
    static class BuildModeHarness
    {
        public const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        public static readonly Type AppType = Type.GetType("CutOnce.Device.CutOnceApp, Assembly-CSharp");
        public static Type BuildType => Type.GetType("CutOnce.Device.BuildMode, Assembly-CSharp");

        // ── this machine's config and journal are moved aside; the app gets a closed port and, unless asked not to, a first run ──
        public sealed class Isolation
        {
            readonly string _config = Path.Combine(Application.persistentDataPath, "cutonce.config.json"), _journal = Path.Combine(Application.persistentDataPath, "cutonce-builds");
            string ConfigBackup => _config + ".before-build-test";
            string JournalBackup => _journal + ".before-build-test";

            /// <param name="firstRun">The app ships no plan, so a test that needs a hologram showing before build mode gets
            /// one the way a headset that built before has one: a journal holding a run of the desk test plan (data/demo).</param>
            public Isolation(bool firstRun = true)
            {
                // A backup already there is from a run that crashed before restoring: it holds this machine's real files, so keep
                // it and drop the test leftovers instead of failing every test after it.
                if (File.Exists(ConfigBackup)) File.Delete(_config); else if (File.Exists(_config)) File.Move(_config, ConfigBackup);
                if (Directory.Exists(JournalBackup)) { if (Directory.Exists(_journal)) Directory.Delete(_journal, true); }
                else if (Directory.Exists(_journal)) Directory.Move(_journal, JournalBackup);
                File.WriteAllText(_config, "{\"server_url\":\"http://127.0.0.1:9\",\"api_token\":\"none\",\"device_id\":\"build-test\"}");   // port 9: nothing listens
                if (!firstRun) return;
                string planJson = File.ReadAllText(RepoFile("data", "demo", "desk.plan.json"));
                var plan = CoreJson.Parse<PlanDto>(planJson);
                new Journal(_journal).Save(new Journal.Snapshot
                {
                    assembly = new AssemblyDto { assembly_id = "asm_test_first_run", plan_id = plan.plan_id, plan_revision = plan.revision, name = "Test run", seed = "empty", status = "active" },
                    plan_json = planJson,
                });
            }

            public void Restore()
            {
                if (File.Exists(_config)) File.Delete(_config);
                if (Directory.Exists(_journal)) Directory.Delete(_journal, true);
                if (File.Exists(ConfigBackup)) File.Move(ConfigBackup, _config);
                if (Directory.Exists(JournalBackup)) Directory.Move(JournalBackup, _journal);
            }
        }

        // ── the app ─────────────────────────────────────────────────────────────────────────────────────────────
        public static Component StartApp(string name, bool copilot)
        {
            Assert.That(AppType, Is.Not.Null, "CutOnceApp is missing from Assembly-CSharp");
            Assert.That(BuildType, Is.Not.Null, "BuildMode is missing from Assembly-CSharp");
            var app = new GameObject(name).AddComponent(AppType);
            AppType.GetField("createCopilot").SetValue(app, copilot);
            return app;
        }

        public static IEnumerator UntilTheFirstRunShows()
        {
            for (float waited = 0f; waited < 15f && (Hologram() == null || Hologram().Views.Count == 0); waited += Time.unscaledDeltaTime) yield return null;
            Assert.That(Hologram()?.Views.Count ?? 0, Is.GreaterThan(0), "the first run (the desk test plan, from the journal) never loaded");
        }

        public static IEnumerator Until(Func<bool> done, float seconds, string what)
        {
            for (float waited = 0f; waited < seconds && !done(); waited += Time.unscaledDeltaTime) yield return null;
            Assert.That(done(), Is.True, what);
        }

        public static UnityEngine.Object BuildModeOf() => UnityEngine.Object.FindAnyObjectByType(BuildType);
        public static BuildFlow Flow(UnityEngine.Object mode) => (BuildFlow)BuildType.GetProperty("Flow").GetValue(mode);
        public static BuildPhase Phase(UnityEngine.Object mode) => Flow(mode).Phase;
        public static string ModeOf(Component app) => (string)AppType.GetProperty("Mode").GetValue(app);
        public static AssemblyView Hologram() => UnityEngine.Object.FindAnyObjectByType<AssemblyView>(FindObjectsInactive.Include);
        public static BuildStateStore StoreOf(Component app) => (BuildStateStore)AppType.GetField("_store", Hidden).GetValue(app);
        public static string Toast() => GameObject.Find("[HUD]").transform.Find("toast").GetComponent<Text>().text;

        public static object Call(UnityEngine.Object mode, string method, params object[] args)
        {
            var m = BuildType.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(m, Is.Not.Null, $"BuildMode.{method} is missing");
            return m.Invoke(mode, args);
        }

        public static void Press(UnityEngine.Object mode, ButtonGesture gesture) => Call(mode, "OnScanButton", gesture);

        public static void Set(UnityEngine.Object mode, string field, object value)
        {
            var f = BuildType.GetField(field, Hidden);
            Assert.That(f, Is.Not.Null, $"BuildMode.{field} is missing");
            f.SetValue(mode, value);
        }

        public static T Get<T>(UnityEngine.Object mode, string field) => (T)BuildType.GetField(field, Hidden).GetValue(mode);

        // ── what the server would send ──────────────────────────────────────────────────────────────────────────
        public static void Send(UnityEngine.Object mode, WsMessageDto message) => Call(mode, "OnBuildMessage", message);

        /// <summary>A file in the repository (data/…), found by walking up from the Unity project.</summary>
        public static string RepoFile(params string[] parts)
        {
            for (var dir = new DirectoryInfo(Application.dataPath); dir != null; dir = dir.Parent)
            {
                string path = Path.Combine(dir.FullName, Path.Combine(parts));
                if (File.Exists(path)) return path;
            }
            throw new FileNotFoundException(string.Join("/", parts) + " was not found above " + Application.dataPath);
        }

        static string FixturePath(params string[] parts) => RepoFile(new[] { "data", "fixtures" }.Concat(parts).ToArray());

        public static string IdeasFixtureJson() => File.ReadAllText(FixturePath("build", "ws_build_ideas.json"));

        /// <summary>The message both the TypeScript and the C# tests parse: one idea, "Can on a stage", whose can is twin o1.</summary>
        public static WsMessageDto IdeasFixture() => CoreJson.Parse<WsMessageDto>(IdeasFixtureJson());

        public static TwinDto Can(double[] position) => new TwinDto
        {
            twin_id = "o1", name = "tall_can", label = "tall can", snapped = true, material = "metal",
            shape = new ShapeDto { type = "cylinder", axis = "y", diameter = 0.066, length = 0.157 }, position = position,
        };

        public static WsMessageDto Inventory(double[] canPosition, bool labelled = true, string session = "bsess_fixture")
        {
            var inventory = new InventoryDto { session_id = session, labelled = labelled };
            inventory.twins.Add(Can(canPosition));
            return new WsMessageDto { type = "build_inventory", inventory = inventory };
        }

        /// <summary>
        /// Loads a run of <paramref name="plan"/> the way the app does when the stream says a run started: the store, then the
        /// hologram. A plan with no model files to fetch is built before this returns, so what follows sees the very first
        /// moment of the placement, however slow the frames are.
        /// </summary>
        public static void LoadRun(Component app, PlanDto plan, string assemblyId)
        {
            StoreOf(app).Reset(plan, assemblyId, null);
            var building = (Task)AppType.GetMethod("BuildHologram", Hidden).Invoke(app, null);
            Assert.That(building.IsCompleted && !building.IsFaulted, Is.True, "the hologram was not built at once: " + building.Exception);
        }

        /// <summary>Objects, ideas, the fixture's design picked and its pieces flown in: the walkthrough, as a judge reaches it.</summary>
        public static IEnumerator ReachTheWalkthrough(Component app, UnityEngine.Object mode)
        {
            Send(mode, Inventory(new[] { 0.6, 0.8185, 0.9 }));
            Send(mode, IdeasFixture());
            yield return null;
            // Locking makes a spatial anchor, and anchors only exist on the headset: in the Editor Meta's OVRSpatialAnchor logs
            // an error when it cannot make one. That one log is expected, so errors are let through until the lock settles.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            LoadRun(app, IdeasFixture().ideas[0].plan, "asm_build_smoke");
            yield return Until(() => Phase(mode) == BuildPhase.Walkthrough, 8f, "the fly-together never finished");
            yield return null;
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
        }

        // ── stand-ins for the headset and the server ────────────────────────────────────────────────────────────
        /// <summary>The right controller, as a test holds it.</summary>
        public sealed class FakeInput : IOperatorInput
        {
            public Ray Pointer = new Ray(new Vector3(0f, 1.2f, 0f), Vector3.up);
            public bool HasPointer = true;
            public bool TryGetPointer(out Ray ray) { ray = Pointer; return HasPointer; }
            public Vector3 TipWorld => Pointer.origin;
            public Vector2 Stick { get; set; }
            public bool TriggerDown { get; set; }
            public bool TriggerHeld { get; set; }
            public bool GripHeld { get; set; }
            public bool MarkDown { get; set; }
            public bool MarkHeld { get; set; }
            public bool MarkUp { get; set; }
            public bool StickClickHeld { get; set; }
        }

        /// <summary>One pull of the trigger: build mode's Update sees it for exactly one frame.</summary>
        public static IEnumerator PullTrigger(FakeInput input)
        {
            input.TriggerDown = true;
            yield return null;
            input.TriggerDown = false;
        }

        /// <summary>The depth sensor over an empty table 74 cm high: every ray that points down lands on it.</summary>
        public sealed class TableSurface : ISurfaceRaycaster
        {
            public int Rays;
            public bool Raycast(Ray ray, out Vector3 point) { Rays++; return PlacementMath.HitHorizontalPlane(ray, 0.74f, 6f, out point); }
        }

        /// <summary>The server: remembers what it was asked and answers what the test tells it to (404 until told otherwise).</summary>
        public sealed class StubServer : IHttpTransport
        {
            public readonly List<HttpRequest> Requests = new List<HttpRequest>();
            public Func<HttpRequest, Task<HttpResult>> Answer = _ => Json(404, "{\"error\":{\"code\":\"not_found\",\"message\":\"\"}}");
            public Task<HttpResult> SendAsync(HttpRequest request) { Requests.Add(request); return Answer(request); }
            public static Task<HttpResult> Json(int status, string body) => Task.FromResult(new HttpResult { Status = status, Body = body });
            public List<HttpRequest> To(string pathEnd) => Requests.FindAll(r => r.Url.EndsWith(pathEnd));
        }

        public static StubServer UseStubServer(UnityEngine.Object mode)
        {
            var server = new StubServer();
            Set(mode, "_api", new ApiClient(server, new ServerConfig { server_url = "http://stub", api_token = "t", device_id = "build-test" }));
            return server;
        }

        /// <summary>
        /// Everything a scan needs that the Editor lacks: a camera that gives a picture (in place of the copilot's stored
        /// photo, which is only there after `pnpm sync:fixtures`), depth under every ray, and a server to upload to. The grid
        /// is small so a scan takes one frame unless a test slows it down.
        /// </summary>
        public static IEnumerator ScanRig(UnityEngine.Object mode, Action<TableSurface, StubServer> ready, int raysPerFrame = 1024)
        {
            CopilotController copilot = null;
            for (float waited = 0f; waited < 5f && copilot == null; waited += Time.unscaledDeltaTime) { copilot = UnityEngine.Object.FindAnyObjectByType<CopilotController>(); yield return null; }
            Assert.That(copilot, Is.Not.Null, "the app did not create the copilot (start it with copilot: true)");
            copilot.frameSourceBehaviour = copilot.gameObject.AddComponent<StubFrames>();
            var surface = new TableSurface();
            Set(mode, "_surface", surface);
            var capture = Get<Component>(mode, "_capture");
            foreach (var (field, value) in new[] { ("cols", 16), ("rows", 12), ("raysPerFrame", raysPerFrame) }) capture.GetType().GetField(field).SetValue(capture, value);
            ready(surface, UseStubServer(mode));
        }
    }
}
