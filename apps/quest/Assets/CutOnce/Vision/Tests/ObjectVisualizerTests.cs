using NUnit.Framework;
using UnityEngine;

namespace CutOnce.Vision.Tests
{
    public sealed class ObjectVisualizerTests
    {
        private GameObject _owner;
        private ObjectVisualizer _visualizer;
        private TrackedObject _tracked;
        private bool _previousDebug;

        [SetUp]
        public void SetUp()
        {
            _previousDebug = VisionDebug.Enabled;
            VisionDebug.Enabled = false;
            _owner = new GameObject("Object visualizer test");
            _visualizer = _owner.AddComponent<ObjectVisualizer>();
            _tracked = new TrackedObject
            {
                id = 12,
                className = "bottle",
                confidence = 0.91f,
                smoothedWorldPosition = new Vector3(0f, 1f, 2f),
                smoothedWorldSize = new Vector3(0.1f, 0.25f, 0.1f),
                lastSeenTime = Time.time,
            };
        }

        [TearDown]
        public void TearDown()
        {
            if (_owner != null) Object.DestroyImmediate(_owner);
            VisionDebug.Enabled = _previousDebug;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NoDepthShowsAnHonestLabelAndNeverPaintsTheProxyBox(bool focused)
        {
            _visualizer.Show(_tracked, focused);

            Assert.That(_visualizer.HasLiveSurfaceDepth, Is.False);
            Assert.That(Highlight.enabled, Is.False, "A focus change must not bring back a fallback cube.");
            Assert.That(_tracked.visual.activeSelf, Is.True, "The recognition label stays visible.");
            Assert.That(Label.text, Does.Contain("BOTTLE").And.Contain("DEPTH UNAVAILABLE"));
            Assert.That(Highlight.GetComponent<Collider>(), Is.Null, "A highlight must not intercept room raycasts.");
        }

        [Test]
        public void DiagnosticBoxesRequireOptInAndDisappearWhenDebugIsTurnedOff()
        {
            _visualizer.Show(_tracked);
            var original = _tracked.visual;
            Assert.That(Highlight.enabled, Is.False);

            VisionDebug.Enabled = true;
            _visualizer.Show(_tracked);
            Assert.That(Highlight.enabled, Is.True);
            Assert.That(Highlight.sharedMaterial.shader.name, Is.EqualTo("CutOnce/Hologram"));
            Assert.That(Label.text, Does.Contain("#12").And.Contain("91%"));

            VisionDebug.Enabled = false;
            _visualizer.Show(_tracked);
            Assert.That(_tracked.visual, Is.SameAs(original));
            Assert.That(Highlight.enabled, Is.False);
            Assert.That(Label.text, Does.Contain("DEPTH UNAVAILABLE").And.Not.Contain("#12"));
        }

        [Test]
        public void FocusChangesUpdateTheExistingDiagnosticMaterialProperties()
        {
            VisionDebug.Enabled = true;
            _visualizer.Show(_tracked, false);
            var material = Highlight.sharedMaterial;
            var properties = new MaterialPropertyBlock();
            Highlight.GetPropertyBlock(properties);
            var normalAlpha = properties.GetColor("_FillColor").a;

            _visualizer.Show(_tracked, true);
            Highlight.GetPropertyBlock(properties);
            Assert.That(Highlight.sharedMaterial, Is.SameAs(material), "Focus must not allocate a new material.");
            Assert.That(properties.GetColor("_FillColor").a, Is.GreaterThan(normalAlpha));
            Assert.That(properties.GetFloat("_PulseHz"), Is.GreaterThan(0f));

            _visualizer.Show(_tracked, false);
            Highlight.GetPropertyBlock(properties);
            Assert.That(properties.GetColor("_FillColor").a, Is.EqualTo(normalAlpha));
            Assert.That(properties.GetFloat("_PulseHz"), Is.Zero);
        }

        [Test]
        public void ReleasingARecognizedObjectDestroysItsLabelAndHighlight()
        {
            _visualizer.Show(_tracked);
            var visual = _tracked.visual;
            _visualizer.Release(_tracked);

            Assert.That(_tracked.visual, Is.Null);
            Assert.That(visual == null, Is.True);
        }

        private MeshRenderer Highlight => _tracked.visual.transform.Find("Highlight").GetComponent<MeshRenderer>();
        private TextMesh Label => _tracked.visual.transform.Find("Label").GetComponent<TextMesh>();
    }
}
