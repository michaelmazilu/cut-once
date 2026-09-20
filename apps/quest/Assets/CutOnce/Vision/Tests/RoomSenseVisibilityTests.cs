using System.Collections.Generic;
using System.Reflection;
using CutOnce.RoomSense;
using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class RoomSenseVisibilityTests
    {
        private GameObject _owner;
        private bool _previousPolicy;

        [SetUp]
        public void SetUp()
        {
            _previousPolicy = RoomSenseBootstrap.DetectedObjectsOnly;
            RoomSenseBootstrap.SetDetectedObjectsOnly(false);
            _owner = new GameObject("RoomSense visibility test");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_owner);
            RoomSenseBootstrap.SetDetectedObjectsOnly(_previousPolicy);
        }

        [Test]
        public void StartingVisionHidesExistingRoomOverlaysAndGuessedLabelsWithoutDisablingGeometry()
        {
            var glow = _owner.AddComponent<RoomGlow>();
            var gaze = _owner.AddComponent<GazeInspector>();
            var geometry = new GameObject("Room geometry");
            geometry.transform.SetParent(_owner.transform);
            var collider = geometry.AddComponent<BoxCollider>();

            // Model an overlay already created by an earlier asynchronous room-load callback.
            var overlay = new GameObject("Existing room overlay");
            var highlight = new GameObject("Existing guessed highlight");
            var label = new GameObject("Existing guessed label");
            var spawned = (List<GameObject>)Field(typeof(RoomGlow), "_spawned").GetValue(glow);
            spawned.Add(overlay);
            Field(typeof(GazeInspector), "_highlightGo").SetValue(gaze, highlight);
            Field(typeof(GazeInspector), "_labelGo").SetValue(gaze, label);

            RoomSenseBootstrap.SetDetectedObjectsOnly(true);

            Assert.That(overlay.activeSelf, Is.False);
            Assert.That(highlight.activeSelf, Is.False);
            Assert.That(label.activeSelf, Is.False);
            Assert.That(glow.enabled, Is.True);
            Assert.That(gaze.enabled, Is.True);
            Assert.That(collider.enabled && geometry.activeInHierarchy, Is.True,
                "Rendering policy must not switch off the room's data/collider objects.");
        }

        [Test]
        public void RoomSenseCreatedAfterVisionStillCannotRenderEvenIfItsSceneEnablesGlow()
        {
            RoomSenseBootstrap.SetDetectedObjectsOnly(true);
            var glow = _owner.AddComponent<RoomGlow>();
            var gaze = _owner.AddComponent<GazeInspector>();
            glow.glowEverything = true;
            glow.glowLabelledShapes = true;
            glow.RenderingEnabled = true;
            gaze.RenderingEnabled = true;

            Assert.That(glow.RenderingEnabled, Is.False);
            Assert.That(gaze.RenderingEnabled, Is.False);
            Assert.That(glow.enabled && gaze.enabled, Is.True);
        }

        [Test]
        public void ExplicitRoomSenseDemoCanRestoreRenderingWhenVisionReleasesOwnership()
        {
            var glow = _owner.AddComponent<RoomGlow>();
            var gaze = _owner.AddComponent<GazeInspector>();
            RoomSenseBootstrap.SetDetectedObjectsOnly(true);
            RoomSenseBootstrap.SetDetectedObjectsOnly(false);

            Assert.That(glow.RenderingEnabled, Is.True);
            Assert.That(gaze.RenderingEnabled, Is.True);
            glow.RenderingEnabled = false;
            Assert.That(glow.RenderingEnabled, Is.False);
            Assert.That(glow.enabled, Is.True);
        }

        private static FieldInfo Field(System.Type type, string name)
            => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
}
