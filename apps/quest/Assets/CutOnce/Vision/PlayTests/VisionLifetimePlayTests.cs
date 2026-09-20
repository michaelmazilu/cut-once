using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace CutOnce.Vision.PlayTests
{
    public sealed class VisionLifetimePlayTests
    {
        private GameObject _parent;
        private LifetimeTestScanner.State _state;

        private LifetimeTestScanner Create()
        {
            _parent = new GameObject("Scanner parent lifecycle test");
            var child = new GameObject("Scanner lifecycle test");
            child.transform.SetParent(_parent.transform);
            var scanner = child.AddComponent<LifetimeTestScanner>();
            _state = scanner.Data;
            return scanner;
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            for (var frame = 0; frame < 120 && !condition(); ++frame) yield return null;
            Assert.That(condition(), Is.True, "Controlled operation did not make progress within 120 frames.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_state != null) { _state.Ready = false; _state.CaptureDone = true; _state.InferenceDone = true; }
            if (_parent != null) UnityEngine.Object.Destroy(_parent);
            if (_state != null) yield return Until(() => _state.Disposed);
        }

        [UnityTest]
        public IEnumerator IdleParentReactivationResumesWithoutStartingASecondLoop()
        {
            Create();
            yield return Until(() => _state.LoopStarts == 1);
            _parent.SetActive(false); yield return null;
            _parent.SetActive(true);
            _state.Ready = true;
            yield return Until(() => _state.CapturePending);
            Assert.That(_state.LoopStarts, Is.EqualTo(1));
            Assert.That(_state.Captures, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DisablingScannerWhileCaptureIsPendingDrainsBeforeReuse()
        {
            var scanner = Create(); _state.Ready = true;
            yield return Until(() => _state.CapturePending);
            scanner.gameObject.SetActive(false); yield return null;
            scanner.gameObject.SetActive(true); yield return null;
            Assert.That(_state.Captures, Is.EqualTo(1));
            Assert.That(_state.Inferences, Is.Zero);
            _state.Ready = false;
            _state.CaptureDone = true;
            yield return Until(() => !_state.CapturePending);
            Assert.That(_state.Publications, Is.Zero);
            _state.CaptureDone = false; _state.Ready = true;
            yield return Until(() => _state.Captures == 2);
            Assert.That(_state.LoopStarts, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RapidParentTogglesHoldInferenceLeaseAndSuppressOldPublication()
        {
            Create(); _state.Ready = true; _state.CaptureDone = true;
            yield return Until(() => _state.Inferences == 1);
            for (var index = 0; index < 5; ++index)
            {
                _parent.SetActive(false); yield return null;
                _parent.SetActive(true); yield return null;
            }
            Assert.That(_state.LeaseHeld, Is.True);
            Assert.That(_state.Releases, Is.Zero);
            Assert.That(_state.Inferences, Is.EqualTo(1));
            _state.Ready = false; _state.InferenceDone = true;
            yield return Until(() => !_state.LeaseHeld);
            Assert.That(_state.Publications, Is.Zero);
            Assert.That(_state.Releases, Is.EqualTo(1));
            _state.Ready = true; _state.InferenceDone = false;
            yield return Until(() => _state.Inferences == 2);
            Assert.That(_state.LoopStarts, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ComponentDisableKeepsTheSamePendingOperation()
        {
            var scanner = Create(); _state.Ready = true; _state.CaptureDone = true;
            yield return Until(() => _state.Inferences == 1);
            scanner.enabled = false; yield return null;
            scanner.enabled = true; yield return null;
            Assert.That(_state.Inferences, Is.EqualTo(1));
            Assert.That(_state.LeaseHeld, Is.True);
            _state.Ready = false; _state.InferenceDone = true;
            yield return Until(() => _state.Releases == 1);
            Assert.That(_state.Publications, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DestroyedOwnerRetiresOnlyAfterInferenceAndLeaseDrain()
        {
            Create(); _state.Ready = true; _state.CaptureDone = true;
            yield return Until(() => _state.Inferences == 1);
            UnityEngine.Object.Destroy(_parent); yield return null; yield return null;
            Assert.That(_state.Disposed, Is.False);
            Assert.That(_state.LeaseHeld, Is.True);
            _state.InferenceDone = true;
            yield return Until(() => _state.Disposed);
            Assert.That(_state.LeaseHeld, Is.False);
            Assert.That(_state.Releases, Is.EqualTo(1));
            Assert.That(_state.Publications, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DestroyedOwnerWithPendingCaptureCannotPublishOrDisposeEarly()
        {
            Create(); _state.Ready = true;
            yield return Until(() => _state.CapturePending);
            UnityEngine.Object.Destroy(_parent); yield return null; yield return null;
            Assert.That(_state.Disposed, Is.False);
            _state.CaptureDone = true;
            yield return Until(() => _state.Disposed);
            Assert.That(_state.Inferences, Is.Zero);
            Assert.That(_state.Publications, Is.Zero);
        }

        [UnityTest]
        public IEnumerator SceneUnloadDoesNotDestroyTheIndependentDrainPump()
        {
            Create(); _state.Ready = true; _state.CaptureDone = true;
            var scene = SceneManager.CreateScene("Vision lifetime unload regression");
            SceneManager.MoveGameObjectToScene(_parent, scene);
            yield return Until(() => _state.Inferences == 1);
            var unload = SceneManager.UnloadSceneAsync(scene);
            while (!unload.isDone) yield return null;
            Assert.That(_state.Retire, Is.True);
            Assert.That(_state.Disposed, Is.False);
            Assert.That(_state.LeaseHeld, Is.True);
            _state.InferenceDone = true;
            yield return Until(() => _state.Disposed);
            Assert.That(_state.Releases, Is.EqualTo(1));
            Assert.That(_state.Publications, Is.Zero);
        }
    }
}
