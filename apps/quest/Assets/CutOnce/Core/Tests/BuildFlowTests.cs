using System.Collections.Generic;
using NUnit.Framework;

namespace CutOnce.Core.Tests
{
    public class BuildFlowTests
    {
        static BuildIdeaDto Idea(string id, string plan) => new BuildIdeaDto { idea_id = id, session_id = "bsess_a", title = id, plan = new PlanDto { plan_id = plan } };
        static InventoryDto Inv(bool labelled, string session = "bsess_a") => new InventoryDto { session_id = session, labelled = labelled };

        [Test]
        public void ScanToWalkthroughInOrder()
        {
            var f = new BuildFlow();
            f.StartScan();                                  Assert.That(f.Phase, Is.EqualTo(BuildPhase.Scanning));
            f.OnScanAccepted(f.ScanTicket, "bsess_a");
            f.OnInventory(Inv(false));                      Assert.That(f.Phase, Is.EqualTo(BuildPhase.Scanning));
            f.OnInventory(Inv(true));                       Assert.That(f.Phase, Is.EqualTo(BuildPhase.Labelled));
            f.OnIdeas("bsess_a", new List<BuildIdeaDto> { Idea("idea_1", "plan_build_1") }, false);
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Ideas));
            Assert.That(f.Pick("idea_1"), Is.True);         Assert.That(f.Phase, Is.EqualTo(BuildPhase.Starting));
            Assert.That(f.TryPlace("plan_other"), Is.False);
            Assert.That(f.TryPlace("plan_build_1"), Is.True);
            f.OnPlaced();                                   Assert.That(f.Phase, Is.EqualTo(BuildPhase.Assembling));
            f.OnAssembled();                                Assert.That(f.Phase, Is.EqualTo(BuildPhase.Walkthrough));
            f.OnInventory(Inv(true));                       Assert.That(f.Phase, Is.EqualTo(BuildPhase.Walkthrough), "a late inventory does not interrupt the build");
        }

        [Test]
        public void ARunStartedByVoiceOrTheDirectorIsPlacedToo()
        {
            var f = new BuildFlow();
            f.OnInventory(Inv(true));
            f.OnIdeas("bsess_a", new List<BuildIdeaDto> { Idea("idea_1", "plan_build_1"), Idea("idea_2", "plan_build_2") }, true);
            Assert.That(f.TryPlace("plan_build_2"), Is.True);
            Assert.That(f.Chosen.idea_id, Is.EqualTo("idea_2"));
        }

        [Test]
        public void ADesignStartedWhileAnotherViewIsBeingScannedIsStillPlaced()
        {
            var f = new BuildFlow();
            f.OnInventory(Inv(true));
            f.OnIdeas("bsess_a", new List<BuildIdeaDto> { Idea("idea_1", "plan_build_1") }, true);
            f.StartScan();                                  // a look-around scan is running when "build the can stage" is said
            Assert.That(f.TryPlace("plan_other"), Is.False, "not one of the ideas");
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Scanning));
            Assert.That(f.TryPlace("plan_build_1"), Is.True);
            Assert.That(new object[] { f.Phase, f.Chosen.idea_id }, Is.EqualTo(new object[] { BuildPhase.Starting, "idea_1" }));
            f.OnPlaced(); f.OnAssembled();
            Assert.That(f.TryPlace("plan_build_1"), Is.False, "already built: a reload is not a second fly-together");
            Assert.That(new BuildFlow().TryPlace("plan_build_1"), Is.False, "build mode is off");
        }

        [Test]
        public void IdeasFromAnotherSessionAreIgnoredAndExitResets()
        {
            var f = new BuildFlow();
            f.StartScan(); f.OnScanAccepted(f.ScanTicket, "bsess_a"); f.OnInventory(Inv(true));
            f.OnIdeas("bsess_b", new List<BuildIdeaDto> { Idea("idea_x", "plan_x") }, true);
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Labelled));
            f.Exit();
            Assert.That(new object[] { f.Phase, f.SessionId, f.Active }, Is.EqualTo(new object[] { BuildPhase.Off, null, false }));
        }

        /// <summary>A flow taken to <paramref name="phase"/> the way the headset gets there.</summary>
        static BuildFlow At(BuildPhase phase)
        {
            var f = new BuildFlow();
            if (phase == BuildPhase.Off) return f;
            f.StartScan(); f.OnScanAccepted(f.ScanTicket, "bsess_a");
            if (phase == BuildPhase.Scanning) return f;
            f.OnInventory(Inv(true));
            if (phase == BuildPhase.Labelled) return f;
            f.OnIdeas("bsess_a", new List<BuildIdeaDto> { Idea("idea_1", "plan_build_1") }, true);
            if (phase == BuildPhase.Ideas) return f;
            f.Pick("idea_1");
            if (phase == BuildPhase.Starting) return f;
            f.TryPlace("plan_build_1"); f.OnPlaced();
            if (phase == BuildPhase.Assembling) return f;
            f.OnAssembled();
            return f;
        }

        [TestCase(BuildPhase.Off, true)]
        [TestCase(BuildPhase.Scanning, true)]
        [TestCase(BuildPhase.Labelled, true)]
        [TestCase(BuildPhase.Ideas, true)]
        [TestCase(BuildPhase.Starting, false)]
        [TestCase(BuildPhase.Assembling, false)]
        [TestCase(BuildPhase.Walkthrough, false)]
        public void TheScanButtonIsIgnoredOnceADesignIsBeingBuilt(BuildPhase phase, bool scans)
        {
            // A judge holds both controllers while placing a real can: a thumb on X must not throw the walkthrough away.
            // Saying "what can I build?" stays the deliberate way to start over.
            var f = At(phase);
            Assert.That(f.Phase, Is.EqualTo(phase));
            Assert.That(f.CanScanFromButton, Is.EqualTo(scans));
        }

        [TestCase(BuildPhase.Off, false)]
        [TestCase(BuildPhase.Scanning, false)]
        [TestCase(BuildPhase.Labelled, true)]
        [TestCase(BuildPhase.Ideas, false)]
        [TestCase(BuildPhase.Starting, false)]
        [TestCase(BuildPhase.Assembling, false)]
        [TestCase(BuildPhase.Walkthrough, false)]
        public void TheTriggerOnEmptySpaceOnlyScansWhileThereAreNoPreviewsToMiss(BuildPhase phase, bool scans)
        {
            // With previews showing, a trigger that hits none of them is a near miss (or a hand inside one), not a request to
            // look again: a rescan would clear the very previews being picked from. X is the rescan button there.
            Assert.That(At(phase).CanScanFromTrigger, Is.EqualTo(scans));
        }

        [Test]
        public void AMessageSaysWhetherItWasTakenSoOnlyThoseAreShown()
        {
            var f = new BuildFlow();
            Assert.That(f.OnIdeas("bsess_a", new List<BuildIdeaDto> { Idea("idea_1", "plan_build_1") }, true), Is.False, "ideas with build mode off");
            Assert.That(f.OnInventory(null), Is.False);
            Assert.That(f.OnInventory(Inv(true)), Is.True);
            Assert.That(f.OnIdeas("bsess_b", new List<BuildIdeaDto> { Idea("idea_x", "plan_x") }, true), Is.False, "another session's ideas");
            Assert.That(f.OnIdeas("bsess_a", new List<BuildIdeaDto> { Idea("idea_1", "plan_build_1") }, true), Is.True);
            f.Pick("idea_1");
            Assert.That(f.OnInventory(Inv(true)), Is.False, "nothing interrupts a build");
            Assert.That(f.OnIdeas("bsess_a", new List<BuildIdeaDto>(), true), Is.False);
        }

        [Test]
        public void WhenARethinkLeavesNoIdeasYouAreBackToTheLabelledObjects()
        {
            var f = new BuildFlow();
            f.OnInventory(Inv(true));
            f.OnIdeas("bsess_a", new List<BuildIdeaDto> { Idea("idea_1", "plan_build_1") }, true);
            f.OnIdeas("bsess_a", new List<BuildIdeaDto>(), true);
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Labelled), "nothing to pick, so nothing pretends to be pickable");
            Assert.That(f.Pick("idea_1"), Is.False);
        }

        [Test]
        public void AFailedScanPutsYouBackWhereYouWere()
        {
            var f = new BuildFlow();
            f.StartScan(); f.ScanFailed(f.ScanTicket);
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Off), "the first scan failed: build mode never started");

            f.OnInventory(Inv(true));
            f.OnIdeas("bsess_a", new List<BuildIdeaDto> { Idea("idea_1", "plan_build_1") }, true);
            f.StartScan(); f.ScanFailed(f.ScanTicket);                  // a look-around scan (trigger on empty space) that failed
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Ideas), "the ideas are still there to pick from");

            f.Pick("idea_1"); f.TryPlace("plan_build_1"); f.OnPlaced(); f.OnAssembled();
            f.StartScan(); f.ScanFailed(f.ScanTicket);                  // "what can I build?" said mid-build, and the scan failed
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Walkthrough), "carry on building");
            f.ScanFailed(f.ScanTicket);
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Walkthrough), "a failure with no scan running changes nothing");
        }

        [Test]
        public void OneScanAtATimeSoTwoNeverOpenTwoSessions()
        {
            // The session comes back with the first scan's 202. X pressed again before that sent a second scan with no
            // session, and the server opened a second one: two sets of objects and ideas, each ignoring the other's.
            var f = new BuildFlow();
            Assert.That(f.StartScan(), Is.True);
            Assert.That(f.ScanInFlight, Is.True);
            Assert.That(f.StartScan(), Is.False, "until the server has answered the first");
            Assert.That(f.OnScanAccepted(f.ScanTicket, "bsess_a"), Is.True);
            Assert.That(new object[] { f.ScanInFlight, f.SessionId }, Is.EqualTo(new object[] { false, "bsess_a" }));
            Assert.That(f.StartScan(), Is.True, "another view, sent with the session the first one opened");
            f.ScanFailed(f.ScanTicket);
            Assert.That(f.ScanInFlight, Is.False);
            Assert.That(f.StartScan(), Is.True, "and a failed scan does not block the next");
        }

        [Test]
        public void AScanThatLandsAfterYouLeftDoesNotSwitchBuildModeBackOn()
        {
            var f = new BuildFlow();
            f.StartScan(); int first = f.ScanTicket;
            f.Exit();                                       // the Director cleared the build while the first scan was still uploading
            Assert.That(f.ScanInFlight, Is.False, "leaving forgets the scan, so X works again at once");
            Assert.That(f.OnScanAccepted(first, "bsess_late"), Is.False, "its 202 arrives after all");
            Assert.That(new object[] { f.Active, f.SessionId }, Is.EqualTo(new object[] { false, null }));
            Assert.That(f.OnInventory(Inv(true, "bsess_late")), Is.False, "and its objects do not start build mode over the run that took its place");
            Assert.That(f.Active, Is.False);

            f.StartScan();                                  // X, deliberately: a new scan while the old one's results are still trickling in
            f.ScanFailed(first);
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Scanning), "the old scan's failure is not this scan's");
            Assert.That(f.OnScanAccepted(first, "bsess_later_still"), Is.False);
            Assert.That(f.OnInventory(Inv(true, "bsess_late")), Is.False, "the session that was left stays left, whatever the phase");
            Assert.That(new object[] { f.Phase, f.SessionId, f.ScanInFlight }, Is.EqualTo(new object[] { BuildPhase.Scanning, null, true }));
            Assert.That(f.OnScanAccepted(f.ScanTicket, "bsess_new"), Is.True);
            Assert.That(f.OnInventory(Inv(true, "bsess_new")), Is.True);
        }

        [Test]
        public void IdeasTheServerNoLongerHasAreDroppedSoNothingUnstartableIsShown()
        {
            // The start answered 404: the server restarted, or the Director page opened a new session or replayed a scan. Every
            // preview of that session would answer 404 for ever.
            var f = At(BuildPhase.Starting);
            f.PickGone();
            Assert.That(new object[] { f.Phase, f.Ideas.Count, f.Chosen }, Is.EqualTo(new object[] { BuildPhase.Labelled, 0, null }));
            Assert.That(f.CanScanFromButton, Is.True, "X scans again");

            var walking = At(BuildPhase.Walkthrough);
            walking.PickGone();
            Assert.That(walking.Phase, Is.EqualTo(BuildPhase.Walkthrough), "only a start that is waiting can be gone");
        }

        [Test]
        public void AnotherSessionsObjectsDropTheIdeasOfTheSessionBefore()
        {
            var f = At(BuildPhase.Ideas);
            Assert.That(f.OnInventory(Inv(true, "bsess_replay")), Is.True);      // the Director replayed a recorded scan
            Assert.That(new object[] { f.Phase, f.Ideas.Count, f.SessionId }, Is.EqualTo(new object[] { BuildPhase.Labelled, 0, "bsess_replay" }));

            var unlabelled = At(BuildPhase.Ideas);
            unlabelled.OnInventory(Inv(false, "bsess_replay"));
            Assert.That(new object[] { unlabelled.Phase, unlabelled.Ideas.Count }, Is.EqualTo(new object[] { BuildPhase.Scanning, 0 }));

            var same = At(BuildPhase.Ideas);
            same.OnInventory(Inv(true));                                         // another view merged into the same session
            Assert.That(new object[] { same.Phase, same.Ideas.Count }, Is.EqualTo(new object[] { BuildPhase.Ideas, 1 }), "its ideas stand until new ones arrive");
        }

        static BuildSessionSnapshotDto Snapshot(string session, bool twins, bool ideas)
        {
            var s = new BuildSessionSnapshotDto { session = session == null ? null : new BuildSessionInfoDto { session_id = session } };
            if (twins) s.twins.Add(new TwinDto { twin_id = "o1", name = "tall_can", label = "tall can" });
            if (ideas) s.ideas.Add(Idea("idea_1", "plan_build_1"));
            return s;
        }

        [Test]
        public void AfterAReconnectTheSessionsObjectsAndIdeasAreCaughtUpOn()
        {
            // The 202 came, then the Wi-Fi dropped for the half minute the labels and the ideas took. The stream only resends
            // the current run on a reconnect, so the headset asks for the session and feeds it through the same two doors.
            var f = At(BuildPhase.Scanning);
            var messages = f.CatchUp(Snapshot("bsess_a", twins: true, ideas: true));
            Assert.That(messages.ConvertAll(m => m.type), Is.EqualTo(new[] { "build_inventory", "build_ideas" }));
            Assert.That(new object[] { messages[0].inventory.session_id, messages[0].inventory.labelled, messages[0].inventory.twins.Count }, Is.EqualTo(new object[] { "bsess_a", true, 1 }),
                "the server only keeps objects it has named, so what it holds is labelled");
            Assert.That(new object[] { messages[1].session_id, messages[1].ideas.Count, messages[1].final }, Is.EqualTo(new object[] { "bsess_a", 1, false }), "not final: nothing is said twice");

            Assert.That(f.OnInventory(messages[0].inventory), Is.True);
            Assert.That(f.OnIdeas(messages[1].session_id, messages[1].ideas, messages[1].final), Is.True);
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Ideas));
        }

        [Test]
        public void ASnapshotOnlyCatchesUpTheSessionThisHeadsetIsIn()
        {
            Assert.That(At(BuildPhase.Scanning).CatchUp(Snapshot("bsess_other", true, true)), Is.Empty, "another session");
            Assert.That(At(BuildPhase.Scanning).CatchUp(Snapshot(null, false, false)), Is.Empty, "the server has no session (it restarted)");
            Assert.That(At(BuildPhase.Scanning).CatchUp(null), Is.Empty, "the server could not be reached");
            Assert.That(new BuildFlow().CatchUp(Snapshot("bsess_a", true, true)), Is.Empty, "build mode is off");
            Assert.That(At(BuildPhase.Walkthrough).CatchUp(Snapshot("bsess_a", true, true)), Is.Empty, "nothing interrupts a build");
            Assert.That(At(BuildPhase.Labelled).CatchUp(Snapshot("bsess_a", true, false)).ConvertAll(m => m.type), Is.EqualTo(new[] { "build_inventory" }), "no ideas yet: they will come on the stream");
            Assert.That(At(BuildPhase.Scanning).CatchUp(Snapshot("bsess_a", false, false)), Is.Empty, "no named objects yet either");
        }

        [Test]
        public void TheSiteIsWhereTheChosenDesignStandsElseWhereTheIdeasWouldGo()
        {
            var chosen = new BuildOriginDto { position = new[] { 0.1, 0.74, 0.5 } };
            var other = new BuildOriginDto { position = new[] { 0.4, 0.74, 0.5 } };
            var f = new BuildFlow();
            Assert.That(f.Site, Is.Null, "nothing scanned: no site");
            f.OnInventory(Inv(true));
            var first = Idea("idea_1", "plan_build_1"); first.origin = other;
            var second = Idea("idea_2", "plan_build_2"); second.origin = chosen;
            f.OnIdeas("bsess_a", new List<BuildIdeaDto> { first, second }, true);
            Assert.That(f.Site, Is.SameAs(other), "while choosing: where the first idea would go");
            f.Pick("idea_2");
            Assert.That(f.Site, Is.SameAs(chosen));
        }

        [Test]
        public void NoScanStartsWhileThePiecesAreFlyingSoAFailedOneCannotStrandThem()
        {
            // Only the flight's end leaves Assembling. A scan started mid-flight stopped the flight, and when that scan failed
            // (it always does in the Editor) the flow came back to Assembling with nothing left to end it.
            var f = At(BuildPhase.Assembling);
            Assert.That(f.StartScan(), Is.False, "by button or by voice: wait the second or two the flight takes");
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Assembling));
            f.ScanFailed(f.ScanTicket);
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Assembling), "a failure with no scan running changes nothing");
            f.OnAssembled();
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Walkthrough));
            Assert.That(f.StartScan(), Is.True, "in the walkthrough, asking again is a deliberate start over");
        }

        [Test]
        public void ALateInventoryFromTheSessionYouLeftDoesNotRestartBuildMode()
        {
            var f = new BuildFlow();
            f.StartScan(); f.OnScanAccepted(f.ScanTicket, "bsess_a"); f.OnInventory(Inv(false));
            f.Exit();                                       // the Director started another run while the labels were on their way
            f.OnInventory(Inv(true));
            Assert.That(f.Active, Is.False, "the labels of the session that was left");
            f.OnInventory(Inv(true, "bsess_replay"));
            Assert.That(f.Phase, Is.EqualTo(BuildPhase.Labelled), "a new session (a Director replay) starts build mode");
        }

        [Test]
        public void TheOldHologramIsHiddenWhileYouLookAtTheRoomAndBackForTheBuild()
        {
            var f = new BuildFlow();
            var hidden = new List<BuildPhase>();
            void Note() { if (f.HidesHologram) hidden.Add(f.Phase); }
            Note(); f.StartScan(); Note(); f.OnInventory(Inv(true)); Note();
            f.OnIdeas("bsess_a", new List<BuildIdeaDto> { Idea("idea_1", "plan_build_1") }, true); Note();
            f.Pick("idea_1"); Note(); f.TryPlace("plan_build_1"); f.OnPlaced(); Note(); f.OnAssembled(); Note();
            Assert.That(hidden, Is.EqualTo(new[] { BuildPhase.Scanning, BuildPhase.Labelled, BuildPhase.Ideas, BuildPhase.Starting }));
        }

        [Test]
        public void ADesignThatTurnsABoxKnowsWhichOfItsSidesIsWhichOnTheRealOne()
        {
            // The real box lies flat: 30 long (x), 2 high (y), 20 deep (z). Orientation is never a rotation in a build plan: the
            // design reorders the size instead, so the flight has to work out which design axis is which real axis.
            var real = new[] { 0.30, 0.02, 0.20 };
            Assert.That(FlyPath.MatchAxes(new[] { 0.30, 0.02, 0.20 }, real), Is.EqualTo(new[] { 0, 1, 2 }), "flat, as found");
            Assert.That(FlyPath.MatchAxes(new[] { 0.02, 0.30, 0.20 }, real), Is.EqualTo(new[] { 1, 0, 2 }), "stood upright on its short edge");
            Assert.That(FlyPath.MatchAxes(new[] { 0.20, 0.02, 0.30 }, real), Is.EqualTo(new[] { 2, 1, 0 }), "flat, turned a quarter turn");
            Assert.That(FlyPath.MatchAxes(new[] { 0.201, 0.30, 0.019 }, real), Is.EqualTo(new[] { 2, 0, 1 }), "sizes fixed to a standard differ by millimetres");
            Assert.That(FlyPath.MatchAxes(new[] { 0.1, 0.1, 0.1 }, new[] { 0.1, 0.1, 0.1 }), Is.EqualTo(new[] { 0, 1, 2 }), "a cube needs no turn");
            Assert.That(FlyPath.MatchAxes(null, real), Is.EqualTo(new[] { 0, 1, 2 }), "nothing to match: no turn");
        }

        [Test]
        public void FlightsEaseStaggerAndArc()
        {
            Assert.That(new[] { FlyPath.Ease(0), FlyPath.Ease(0.5), FlyPath.Ease(1) }, Is.EqualTo(new[] { 0.0, 0.5, 1.0 }));
            Assert.That(FlyPath.Progress(1, 0.3), Is.EqualTo(0.0));
            Assert.That(FlyPath.Progress(1, 1.0), Is.EqualTo(1.0));
            Assert.That(FlyPath.Lift(0, 1), Is.EqualTo(0.0));
            Assert.That(FlyPath.Lift(0.5, 1), Is.EqualTo(0.4).Within(1e-9));
            Assert.That(FlyPath.TotalSeconds(4), Is.EqualTo(1.6).Within(1e-9));
        }
    }
}
