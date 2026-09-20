using System;
using System.Collections;
using System.Collections.Generic;

namespace CutOnce.Vision
{
    /// <summary>
    /// The retained state of one vision routine. Deliberately supports only null (next update)
    /// and nested IEnumerator yields used by this pipeline, not Unity wait instructions/tasks.
    /// Its lifetime is independent of the MonoBehaviour that requested it.
    /// </summary>
    public sealed class VisionInferenceOperation
    {
        private readonly Stack<IEnumerator> _frames = new Stack<IEnumerator>(4);
        private readonly Action<Exception> _onError;
        public bool IsComplete { get; private set; }
        public Exception Error { get; private set; }

        public VisionInferenceOperation(IEnumerator routine, Action<Exception> onError = null)
        {
            _frames.Push(routine ?? throw new ArgumentNullException(nameof(routine)));
            _onError = onError;
        }

        /// <summary>Advance once per driver update. A completed operation is inert.</summary>
        public void Tick()
        {
            if (IsComplete) return;
            try
            {
                while (_frames.Count > 0)
                {
                    var frame = _frames.Peek();
                    if (!frame.MoveNext())
                    {
                        _frames.Pop();
                        (frame as IDisposable)?.Dispose();
                        continue;
                    }
                    if (frame.Current is IEnumerator nested) { _frames.Push(nested); continue; }
                    if (frame.Current != null)
                        throw new InvalidOperationException("Vision routines support only null or nested IEnumerator yields.");
                    return;
                }
                IsComplete = true;
            }
            catch (Exception error)
            {
                Error = error;
                // Iterator finally blocks release consumer leases, never the worker itself.
                // The detector's separate backend-drain flag gates actual resource retirement.
                while (_frames.Count > 0)
                {
                    try { (_frames.Pop() as IDisposable)?.Dispose(); }
                    catch (Exception cleanupError) { Error = new AggregateException(Error, cleanupError); }
                }
                IsComplete = true;
                try { _onError?.Invoke(Error); }
                catch (Exception observerError) { Error = new AggregateException(Error, observerError); }
            }
        }
    }
}
