using System;
using System.Collections;
using CutOnce.AR;
using CutOnce.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CutOnce.Device.PlayTests
{
    /// <summary>
    /// Starts the whole app the way a scene does, with no server and no headset, and checks that the hologram and
    /// the HUD come up with no error logged. It is the test that catches a missing Resources file, a stripped shader
    /// or a broken wiring order, which no unit test can. CutOnceApp lives in Assembly-CSharp (it names Meta types),
    /// which a test assembly cannot reference, so it is found by name.
    /// </summary>
    public class AppSmokeTests
    {
        /// <summary>
        /// Every test starts from an empty scene, even after a failure: destroy everything an app or a copilot created
        /// (the app makes AssemblyRoot, [Pointer] and the HUD as separate root objects). A leftover copilot would make
        /// the next app skip building its own.
        /// </summary>
        BuildModeHarness.Isolation _isolation;

        [UnityTearDown]
        public IEnumerator DestroyWhatTheAppCreated()
        {
            DestroyAppObjects();
            yield return null;                                                   // Destroy takes effect at the end of the frame
            yield return null;
            _isolation?.Restore(); _isolation = null;                      // after the app is gone, so it writes no journal afterwards
        }

        /// <summary>Shared with the build-mode tests. Build mode's two roots go with their app; by name too, in case its OnDestroy never ran.</summary>
        public static void DestroyAppObjects()
        {
            LogAssert.ignoreFailingMessages = false;
            var app = Type.GetType("CutOnce.Device.CutOnceApp, Assembly-CSharp");
            foreach (var type in new[] { app, typeof(CutOnce.Copilot.CopilotController), typeof(AssemblyView), typeof(HudController), typeof(VoiceAssistantHud), typeof(SelectionController) })
                if (type != null)
                    foreach (var found in UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None))
                        UnityEngine.Object.Destroy(((Component)found).gameObject);
            foreach (var name in new[] { "[BuildTwins]", "[BuildIdeas]" }) { var left = GameObject.Find(name); if (left != null) UnityEngine.Object.Destroy(left); }
        }

        [Test]
        public void ActiveBuildHudOnlyShowsObjectProgressAndControls()
        {
            var hud = HudController.Create(null);
            try
            {
                var plan = new CutOnce.Core.PlanDto { name = "Workbench" };
                plan.parts.Add(new CutOnce.Core.PartDto { part_id = "top", name = "Top" });
                var state = new CutOnce.Core.BuildStateDto();
                state.progress.pct = 40;

                hud.ShowState(plan, state, new System.Collections.Generic.List<CutOnce.Core.MaterialLine>(),
                    Array.Empty<CutOnce.Core.BuildEventDto>(), "1:200");

                Assert.That(hud.transform.Find("title").GetComponent<UnityEngine.UI.Text>().text, Is.EqualTo("Workbench"));
                Assert.That(hud.transform.Find("bar").gameObject.activeSelf, Is.True);
                Assert.That(hud.transform.Find("bar").GetComponent<RectTransform>().sizeDelta.x, Is.EqualTo(166.4f).Within(.01f));
                Assert.That(hud.transform.Find("hint").GetComponent<UnityEngine.UI.Text>().text, Does.Contain("B: mark / undo"));
                hud.ShowPlacement("Visible surfaces match.");
                hud.ShowStatus("Online", "Looking for the saved position…");
                Assert.That(hud.transform.Find("hint").GetComponent<UnityEngine.UI.Text>().text, Does.Not.Contain("saved position"));
                foreach (var hidden in new[] { "status", "progress", "step title", "step body", "part", "answer", "placement", "toast" })
                    Assert.That(hud.transform.Find(hidden).gameObject.activeSelf, Is.False, hidden + " should not clutter an active build");
            }
            finally { UnityEngine.Object.DestroyImmediate(hud.gameObject); }
        }

        [UnityTest]
        public IEnumerator OfflineWithNoJournalTheAppComesUpEmptyAndSaysWhatToDo()
        {
            _isolation = new BuildModeHarness.Isolation(firstRun: false);
            {
                var type = Type.GetType("CutOnce.Device.CutOnceApp, Assembly-CSharp");
                Assert.That(type, Is.Not.Null, "CutOnceApp is missing from Assembly-CSharp");
                var go = new GameObject("[App] (smoke test)");
                var app = go.AddComponent(type);
                type.GetField("createCopilot").SetValue(app, false);          // the copilot needs the headset's camera and microphone
                for (float waited = 0f; waited < 3f; waited += Time.unscaledDeltaTime) yield return null;   // a sync attempt fails in that time

                var assembly = UnityEngine.Object.FindAnyObjectByType<AssemblyView>(FindObjectsInactive.Include);
                Assert.That(assembly, Is.Not.Null, "no AssemblyRoot was created");
                Assert.That(assembly.Views.Count, Is.EqualTo(0), "the app ships no plan: nothing is drawn until Kit builds something");
                Assert.That(UnityEngine.Object.FindAnyObjectByType<HudController>(), Is.Not.Null);
                Assert.That(GameObject.Find("[HUD]").GetComponent<UnityEngine.UI.CanvasScaler>().dynamicPixelsPerUnit,
                    Is.EqualTo(12f), "the distant task HUD needs more font detail than the near voice HUD");
                Assert.That(GameObject.Find("[HUD]").transform.localScale.x, Is.EqualTo(.0013f),
                    "the distant task HUD must stay large enough to read behind a build");
                var status = GameObject.Find("[HUD]").transform.Find("status").GetComponent<UnityEngine.UI.Text>().text;
                var voiceHud = UnityEngine.Object.FindAnyObjectByType<VoiceAssistantHud>();
                Assert.That(voiceHud, Is.Not.Null, "the voice assistant needs its own always-visible HUD");
                Assert.That(voiceHud.GetComponent<UnityEngine.UI.CanvasScaler>().dynamicPixelsPerUnit,
                    Is.EqualTo(8f), "the voice HUD must rasterise text at VR-readable resolution");
                var hint = voiceHud.transform.Find("hint").GetComponent<UnityEngine.UI.Text>().text;
                // Kit's instructions belong to the view-locked voice HUD, not the task panel beside a table or build.
                Assert.That(hint, Does.Contain("A"), "the voice HUD must name the button that talks to Kit");
                Assert.That(hint, Is.EqualTo(type.GetField("IdleHint").GetValue(null)));
                Assert.That(status, Does.Not.Contain("Kit"));
                Assert.That(voiceHud.transform.Find("answer").GetComponent<UnityEngine.UI.Text>().text, Does.Contain("What can I build"),
                    "the startup voice tip must move with the rest of Kit's UI");
                Assert.That(GameObject.Find("[HUD]").transform.Find("toast").GetComponent<UnityEngine.UI.Text>().text, Does.Not.Contain("What can I build"));
                Assert.That(voiceHud.transform.parent, Is.SameAs(go.transform), "the app owns and cleans up its voice HUD");
                UnityEngine.Object.Destroy(go);
            }
        }

        [UnityTest]
        public IEnumerator TheCopilotComesUpInsideTheAppWithTheStoredPhotoInTheEditor()
        {
            var type = Type.GetType("CutOnce.Device.CutOnceApp, Assembly-CSharp");
            var go = new GameObject("[App] (copilot smoke test)");
            var app = go.AddComponent(type);                                   // createCopilot defaults to true

            CutOnce.Copilot.CopilotController copilot = null;
            for (float waited = 0f; waited < 5f && copilot == null; waited += Time.unscaledDeltaTime)
            {
                copilot = UnityEngine.Object.FindAnyObjectByType<CutOnce.Copilot.CopilotController>();
                yield return null;
            }

            Assert.That(copilot, Is.Not.Null, "the app did not create the copilot");
            Assert.That(copilot.hostBehaviour, Is.SameAs(app), "the app is the copilot's host");
            Assert.That(copilot.frameSourceBehaviour, Is.InstanceOf<CutOnce.Copilot.Capture.FixtureFrameSource>(),
                "the Editor asks with the stored photo, never the headset camera (AGENTS rule 1)");
            Assert.That(copilot.pushToTalkBehaviour, Is.InstanceOf<CutOnce.Copilot.IPushToTalk>());
            Assert.That(copilot.mic, Is.Not.Null);
            Assert.That(copilot.speaker, Is.Not.Null);

            UnityEngine.Object.Destroy(copilot.gameObject);
            UnityEngine.Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator ACopilotPlacedInTheSceneIsRewiredAndStillGetsItsPermissionsAskedFor()
        {
            // Rhythm's [Copilot] prefab in Main.unity is the other way a copilot arrives; it needs the camera and mic too.
            LogAssert.ignoreFailingMessages = true;                            // this bare copilot has no host or camera wired
            var placed = new GameObject("[Copilot] (placed in the scene)").AddComponent<CutOnce.Copilot.CopilotController>();
            var permissions = Type.GetType("CutOnce.Device.QuestPermissions, Assembly-CSharp");
            Assert.That(permissions, Is.Not.Null, "QuestPermissions is missing from Assembly-CSharp");
            int before = (int)permissions.GetProperty("RequestCount").GetValue(null);

            var type = Type.GetType("CutOnce.Device.CutOnceApp, Assembly-CSharp");
            var go = new GameObject("[App] (scene copilot test)");
            var app = go.AddComponent(type);
            type.GetField("createCopilot").SetValue(app, false);                  // the app must not build a second one
            for (float waited = 0f; waited < 15f; waited += Time.unscaledDeltaTime)  // let start-up finish before tearing down
            {
                var assembly = UnityEngine.Object.FindAnyObjectByType<AssemblyView>();
                if (assembly != null && assembly.Views.Count > 0) break;
                yield return null;
            }

            Assert.That((int)permissions.GetProperty("RequestCount").GetValue(null), Is.EqualTo(before + 1),
                "the app asked for the camera and microphone for the copilot already in the scene");
            Assert.That(placed.hostBehaviour, Is.SameAs(app), "a placed prefab kept its empty/stale host");
            Assert.That(placed.frameSourceBehaviour, Is.InstanceOf<CutOnce.Copilot.Capture.FixtureFrameSource>(),
                "build scans and Kit must share a working camera source");
            Assert.That(placed.pushToTalkBehaviour, Is.InstanceOf<CutOnce.Copilot.IPushToTalk>());
            Assert.That(placed.mic, Is.Not.Null);
            Assert.That(placed.speaker, Is.Not.Null);
            LogAssert.ignoreFailingMessages = false;
            UnityEngine.Object.Destroy(placed.gameObject);
            UnityEngine.Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator AnUnmeasuredRecognitionKeepsItsLabelButNotItsGuessedBox()
        {
            var go = new GameObject("[Vision] fallback test");
            var visualizer = go.AddComponent<CutOnce.Vision.ObjectVisualizer>();
            var tracked = new CutOnce.Vision.TrackedObject
            {
                id = 1, className = "bottle", confidence = 0.9f, visible = true,
                smoothedWorldPosition = new Vector3(0f, 1f, 1f), smoothedWorldSize = new Vector3(0.2f, 0.4f, 0.2f),
            };

            visualizer.Show(tracked, focused: false, showGeometry: false);
            yield return null;

            var highlight = tracked.visual.transform.Find("Highlight").GetComponent<MeshRenderer>();
            var label = tracked.visual.transform.Find("Label").GetComponent<TextMesh>();
            Assert.That(highlight.enabled, Is.False, "an unmeasured estimate was drawn as if it were trustworthy geometry");
            Assert.That(label.text, Does.Contain("BOTTLE"), "measurement failure hid passive recognition too");

            visualizer.Show(tracked, focused: false, showGeometry: true);
            Assert.That(highlight.enabled, Is.True, "a later successful measurement did not restore the highlight");
            UnityEngine.Object.Destroy(go);
            yield return null;
        }
    }
}
