using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;

namespace CutOnce.Vision.Tests
{
    /// <summary>Pure .NET operation ownership tests; not Unity execution or GPU-completion proof.</summary>
    public sealed class VisionInferenceOperationTests
    {
        [Test]
        public void NullYieldsAdvanceOnlyOncePerDriverTick()
        {
            var advances = 0;
            IEnumerator Body() { ++advances; yield return null; ++advances; yield return null; ++advances; }
            var operation = new VisionInferenceOperation(Body());
            operation.Tick(); Assert.That(advances, Is.EqualTo(1));
            operation.Tick(); Assert.That(advances, Is.EqualTo(2));
            operation.Tick(); Assert.That(advances, Is.EqualTo(3));
            Assert.That(operation.IsComplete, Is.True);
            operation.Tick(); Assert.That(advances, Is.EqualTo(3));
        }

        [Test]
        public void NestedRoutineCompletesBeforeParentContinuesAndDisposesInsideOut()
        {
            var calls = new List<string>();
            IEnumerator Child() { try { calls.Add("child"); yield return null; } finally { calls.Add("child-finally"); } }
            IEnumerator Parent() { try { calls.Add("parent"); yield return Child(); calls.Add("parent-after"); } finally { calls.Add("parent-finally"); } }
            var operation = new VisionInferenceOperation(Parent());
            operation.Tick();
            Assert.That(calls, Is.EqualTo(new[] { "parent", "child" }));
            operation.Tick(); operation.Tick();
            Assert.That(calls, Is.EqualTo(new[] { "parent", "child", "child-finally", "parent-after", "parent-finally" }));
        }

        [Test]
        public void StoppingAnExternalWaiterDoesNotStopItsRetainedOperation()
        {
            var backendComplete = false;
            var releases = 0;
            IEnumerator Body() { try { while (!backendComplete) yield return null; } finally { ++releases; } }
            var operation = new VisionInferenceOperation(Body());
            IEnumerator Waiter() { while (!operation.IsComplete) yield return null; }
            var waiter = Waiter();
            operation.Tick(); waiter.MoveNext(); (waiter as IDisposable)?.Dispose();
            operation.Tick();
            Assert.That(operation.IsComplete, Is.False);
            Assert.That(releases, Is.Zero);
            backendComplete = true;
            operation.Tick();
            Assert.That(operation.IsComplete, Is.True);
            Assert.That(releases, Is.EqualTo(1));
        }

        [Test]
        public void PauseAndRapidReactivationDoNotDuplicateOrReleasePendingWork()
        {
            var active = true;
            var backendComplete = false;
            var retire = false;
            var generation = 0;
            var starts = 0;
            var publications = 0;
            var releases = 0;
            IEnumerator Work()
            {
                var startedGeneration = generation;
                ++starts;
                try
                {
                    while (!backendComplete) yield return null;
                    if (active && generation == startedGeneration) ++publications;
                }
                finally { ++releases; }
            }
            IEnumerator Loop()
            {
                while (!retire)
                {
                    if (!active) { yield return null; continue; }
                    yield return Work();
                    yield return null;
                }
            }
            var operation = new VisionInferenceOperation(Loop());
            operation.Tick();
            for (var index = 0; index < 10; ++index) { active = !active; ++generation; operation.Tick(); }
            Assert.That(starts, Is.EqualTo(1));
            Assert.That(releases, Is.Zero);
            backendComplete = true;
            operation.Tick();
            Assert.That(publications, Is.Zero);
            Assert.That(releases, Is.EqualTo(1));
            retire = true;
            operation.Tick();
            Assert.That(operation.IsComplete, Is.True);
        }

        [Test]
        public void OwnerRetirementWaitsForPendingWorkBeforeNestedFinally()
        {
            var retired = false;
            var complete = false;
            var disposed = false;
            IEnumerator Work() { while (!complete) yield return null; }
            IEnumerator Owner() { try { yield return Work(); } finally { disposed = true; } }
            var operation = new VisionInferenceOperation(Owner());
            operation.Tick(); retired = true;
            operation.Tick();
            Assert.That(retired, Is.True);
            Assert.That(disposed, Is.False);
            complete = true; operation.Tick();
            Assert.That(disposed, Is.True);
        }

        [Test]
        public void NestedExceptionDisposesIteratorsOnceAndReportsOriginalFailure()
        {
            var disposals = 0;
            Exception observed = null;
            IEnumerator Child() { try { yield return null; throw new InvalidOperationException("after backend completion"); } finally { ++disposals; } }
            IEnumerator Parent() { try { yield return Child(); } finally { ++disposals; } }
            var operation = new VisionInferenceOperation(Parent(), error => observed = error);
            operation.Tick(); operation.Tick(); operation.Tick();
            Assert.That(operation.IsComplete, Is.True);
            Assert.That(disposals, Is.EqualTo(2));
            Assert.That(observed, Is.TypeOf<InvalidOperationException>());
            Assert.That(observed.Message, Is.EqualTo("after backend completion"));
        }

        [Test]
        public void ExceptionDoesNotPretendThatBackendWorkCompleted()
        {
            var backendPending = true;
            var leaseRetained = true;
            IEnumerator Body()
            {
                try { yield return null; throw new InvalidOperationException("request polling failed"); }
                finally { if (!backendPending) leaseRetained = false; }
            }
            var operation = new VisionInferenceOperation(Body());
            operation.Tick(); operation.Tick();
            Assert.That(operation.IsComplete, Is.True);
            Assert.That(backendPending, Is.True);
            Assert.That(leaseRetained, Is.True, "A fault handler must separately drain/quarantine the backend before releasing its lease.");
        }

        [Test]
        public void UnsupportedYieldIsAReportedErrorNotAnInventedWait()
        {
            IEnumerator Body() { yield return new object(); }
            var operation = new VisionInferenceOperation(Body());
            operation.Tick();
            Assert.That(operation.IsComplete, Is.True);
            Assert.That(operation.Error, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void FaultObserverCannotPreventOperationCompletion()
        {
            IEnumerator Body() { yield return new object(); }
            var operation = new VisionInferenceOperation(Body(), _ => throw new Exception("observer failed"));
            Assert.DoesNotThrow(operation.Tick);
            Assert.That(operation.IsComplete, Is.True);
            Assert.That(operation.Error, Is.TypeOf<AggregateException>());
        }
    }
}
