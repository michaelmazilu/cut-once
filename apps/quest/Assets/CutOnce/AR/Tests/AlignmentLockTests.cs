using System.Reflection;
using System.Threading.Tasks;
using CutOnce.AR;
using CutOnce.Core;
using NUnit.Framework;
using UnityEngine;

namespace CutOnce.AR.Tests
{
    /// <summary>
    /// Build mode locks the hologram where the server put the design. That lock is for this session only: the anchor and
    /// the nudge saved for the last build must still be there on the next launch, or the building comes back at last
    /// night's build site.
    /// </summary>
    public class AlignmentLockTests
    {
        const string NudgeKey = "cutonce.alignment.nudge";                     // AlignmentController's saved nudge (PlayerPrefs)

        /// <summary>The headset's anchors, as a test sees them: what was asked for, and two transforms to parent under.</summary>
        sealed class FakeAnchors : IAnchorStore
        {
            public int Saved, Forgotten, ForThisSession;
            public Pose SessionPose;
            public readonly Transform SavedAnchor = new GameObject("[Anchor] saved (fake)").transform, SessionAnchor = new GameObject("[Anchor] session (fake)").transform;
            public Task<Transform> Restore() => Task.FromResult<Transform>(null);
            public Task<Transform> CreateAt(Pose pose) { Saved++; SavedAnchor.SetPositionAndRotation(pose.position, pose.rotation); return Task.FromResult(SavedAnchor); }
            public Task Forget() { Forgotten++; return Task.CompletedTask; }
            public Task<Transform> CreateForSessionAt(Pose pose) { ForThisSession++; SessionPose = pose; SessionAnchor.SetPositionAndRotation(pose.position, pose.rotation); return Task.FromResult(SessionAnchor); }
            public void Destroy() { Object.DestroyImmediate(SavedAnchor.gameObject); Object.DestroyImmediate(SessionAnchor.gameObject); }
        }

        sealed class FakeInput : IOperatorInput
        {
            public Ray Pointer = new Ray(new Vector3(0f, 1.5f, 0f), new Vector3(0f, -1f, 1f).normalized);
            public bool TryGetPointer(out Ray ray) { ray = Pointer; return true; }
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

        GameObject _root; AlignmentController _alignment; FakeAnchors _anchors; FakeInput _input;
        bool _hadNudge; string _nudgeBefore;

        [SetUp]
        public void SetUp()
        {
            // The saved nudge lives in this machine's PlayerPrefs, which the Editor's play mode shares: put back what was there.
            _hadNudge = PlayerPrefs.HasKey(NudgeKey); _nudgeBefore = PlayerPrefs.GetString(NudgeKey, "");
            PlayerPrefs.SetString(NudgeKey, "the last build's saved nudge");

            _root = new GameObject("AssemblyRoot (test)");
            var assembly = _root.AddComponent<AssemblyView>();
            assembly.Build(new PlanDto { plan_id = "plan_test", parts = { new PartDto { part_id = "part_box", name = "box", shape = new ShapeDto { type = "box", size = new[] { 0.2, 0.1, 0.3 } }, position = new[] { 0.0, 0.05, 0.0 } } } });
            _alignment = _root.AddComponent<AlignmentController>();
            _anchors = new FakeAnchors(); _input = new FakeInput();
            _alignment.Init(assembly, _input, null, _anchors);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            _anchors.Destroy();
            if (_hadNudge) PlayerPrefs.SetString(NudgeKey, _nudgeBefore); else PlayerPrefs.DeleteKey(NudgeKey);
        }

        /// <summary>One frame of the controller, as Unity would run it (EditMode has no frame loop).</summary>
        void Frame() => typeof(AlignmentController).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(_alignment, null);

        [Test]
        public void LockAtPutsTheBuildWhereBuildModeSaysAndLocksIt()
        {
            var pose = new Pose(new Vector3(1f, 0.74f, 2f), Quaternion.Euler(0f, 30f, 0f));
            _alignment.LockAt(pose, "build");
            Assert.That(_alignment.State, Is.EqualTo(AlignmentState.Locked));
            Assert.That(_alignment.Method, Is.EqualTo("build"));
            Assert.That(Vector3.Distance(_root.transform.position, pose.position), Is.LessThan(1e-5f));
            Assert.That(Quaternion.Angle(_root.transform.rotation, pose.rotation), Is.LessThan(0.01f));
        }

        [Test]
        public void ABuildModeLockIsForThisSessionOnlyTheSavedAnchorAndNudgeAreLeftAlone()
        {
            var pose = new Pose(new Vector3(1f, 0.74f, 2f), Quaternion.Euler(0f, 30f, 0f));
            _alignment.LockAt(pose, "build");

            Assert.That(new[] { _anchors.Forgotten, _anchors.Saved }, Is.EqualTo(new[] { 0, 0 }), "the anchor saved for the last build was erased or replaced: next launch it comes back at the build site");
            Assert.That(PlayerPrefs.GetString(NudgeKey), Is.EqualTo("the last build's saved nudge"));
            Assert.That(_anchors.ForThisSession, Is.EqualTo(1), "it is still pinned to the room, by an anchor that is never saved");
            Assert.That(Vector3.Distance(_anchors.SessionPose.position, pose.position), Is.LessThan(1e-5f));
            Assert.That(_root.transform.parent, Is.SameAs(_anchors.SessionAnchor));
            Assert.That(Vector3.Distance(_root.transform.position, pose.position), Is.LessThan(1e-5f), "parenting under the anchor keeps the pose");
        }

        [Test]
        public void ANudgeOfABuildModeLockIsNotSavedEither()
        {
            _alignment.LockAt(new Pose(new Vector3(1f, 0.74f, 2f), Quaternion.identity), "build");
            var before = _root.transform.position;
            _input.GripHeld = true; _input.Stick = new Vector2(1f, 0f);
            Frame();                                                            // grip + stick: nudging
            _input.GripHeld = false; _input.Stick = Vector2.zero;
            Frame();                                                            // grip released: this is where a nudge is saved
            Assert.That(PlayerPrefs.GetString(NudgeKey), Is.EqualTo("the last build's saved nudge"),
                "a nudge relative to tonight's build anchor was saved over the desk's: next launch the desk is offset by it");
            Assert.That(_root.transform.position.y, Is.EqualTo(before.y).Within(1e-5f), "(the nudge itself still works: it slides, level)");
        }

        [Test]
        public void PointingAndPullingTheTriggerStillSavesTheAnchorAsBefore()
        {
            _alignment.LockAt(new Pose(new Vector3(1f, 0.74f, 2f), Quaternion.identity), "build");
            _alignment.BeginPlacing();                                          // the operator holds the stick button: place again, by hand
            _input.TriggerDown = true;
            Frame();
            Assert.That(new object[] { _alignment.State, _alignment.Method }, Is.EqualTo(new object[] { AlignmentState.Locked, "pointed" }));
            Assert.That(new[] { _anchors.Forgotten, _anchors.Saved }, Is.EqualTo(new[] { 1, 1 }), "a placement made by hand is the one to come back to");
            Assert.That(_root.transform.parent, Is.SameAs(_anchors.SavedAnchor));
        }
    }
}
