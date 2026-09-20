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
            foreach (var type in new[] { app, typeof(CutOnce.Copilot.CopilotController), typeof(AssemblyView), typeof(HudController), typeof(SelectionController) })
                if (type != null)
                    foreach (var found in UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None))
                        UnityEngine.Object.Destroy(((Component)found).gameObject);
            foreach (var name in new[] { "[BuildTwins]", "[BuildIdeas]" }) { var left = GameObject.Find(name); if (left != null) UnityEngine.Object.Destroy(left); }
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
                var status = GameObject.Find("[HUD]").transform.Find("status").GetComponent<UnityEngine.UI.Text>().text;
                // Nothing is built, so the HUD's job is to say how to start: one button, and Kit does the rest.
                Assert.That(status, Does.Contain("A"), "the idle HUD must name the button that talks to Kit");
                Assert.That(status, Is.EqualTo(type.GetField("IdleHint").GetValue(null)));
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
        public IEnumerator ACopilotPlacedInTheSceneStillGetsItsPermissionsAskedFor()
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
            LogAssert.ignoreFailingMessages = false;
            UnityEngine.Object.Destroy(placed.gameObject);
            UnityEngine.Object.Destroy(go);
        }
    }
}
