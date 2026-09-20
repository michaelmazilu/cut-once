using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace CutOnce.Copilot.Voice
{
    /// <summary>
    /// Plays the answer while the server is still generating it. The audio endpoint returns headerless
    /// 16-bit PCM over a chunked response, so this feeds a streaming AudioClip from a ring of samples as
    /// bytes arrive — that is what gets first audio out inside the 4 s target instead of waiting for the
    /// whole clip.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class PcmStreamPlayer : MonoBehaviour
    {
        public int sampleRate = 22050;

        /// <summary>
        /// How long to keep the clip alive after the last sample has been handed over. The samples OnRead writes are
        /// not heard yet — they are still in the mixer — so stopping the source the instant the queue empties throws
        /// the end of every answer away. OnRead returns silence during the grace, so nothing extra is ever heard.
        /// </summary>
        public float tailGraceSeconds = 0.25f;

        private readonly Queue<float> _pending = new Queue<float>();
        private readonly PcmAssembler _assembler = new PcmAssembler();
        private readonly List<float> _samples = new List<float>(8192);
        private readonly object _lock = new object();
        private AudioSource _source;
        private bool _finished;
        private string _playing;
        /// <summary>At most one queued follow-on: if two things want saying, the newest is the one worth hearing.</summary>
        private string _nextUrl, _nextToken;
        private float _drainedAt;
        /// <summary>Set on the audio thread when the last sample has been handed over; acted on in Update, where Unity's API is safe to call.</summary>
        private volatile bool _drained;

        public bool IsPlaying => _source != null && _source.isPlaying;
        /// <summary>Milliseconds from the request going out to the first sample being queued. This is the G6 number.</summary>
        public float FirstAudioMs { get; private set; } = -1f;

        private void Awake() => _source = GetComponent<AudioSource>();

        /// <summary>
        /// Says this, after whatever is already being said.
        ///
        /// Asking to speak NEVER cuts a sentence off. It used to: Play began with a Stop, so any second speaker
        /// truncated the first mid-word — and there are two of them, the copilot's answers and build mode's step
        /// readouts. Marking a step done with B, or the camera verifying one by itself, would chop Kit mid-sentence.
        /// Interrupting is a real thing to want, but it is a DECISION, so it has its own name: <see cref="Stop"/>.
        /// </summary>
        public void Play(string baseUrl, string audioPath, string bearerToken)
        {
            var url = $"{baseUrl.TrimEnd('/')}{audioPath}";
            if (IsPlaying || _playing != null)
            {
                // Asked for the same answer again while it is still being said: let it finish. Playing it a second
                // time over the first is how one sentence ends up repeating.
                if (url == _playing) { Debug.Log($"[Copilot] already saying {audioPath}; ignoring the repeat request."); return; }
                _nextUrl = url;                       // at most one waits: the newest thing to say is the one that matters
                _nextToken = bearerToken;
                return;
            }
            Begin(url, bearerToken);
        }

        void Begin(string url, string bearerToken)
        {
            StopAllCoroutines();
            if (_source != null) _source.Stop();
            lock (_lock) { _pending.Clear(); _assembler.Reset(); }
            _finished = false;
            _drained = false;
            _drainedAt = 0f;
            FirstAudioMs = -1f;
            _playing = url;
            StartCoroutine(Stream(url, bearerToken));
        }

        /// <summary>Stop talking, now, and drop anything waiting. This is the interrupt: pressing A uses it.</summary>
        public void Stop()
        {
            StopAllCoroutines();
            if (_source != null) _source.Stop();
            lock (_lock) { _pending.Clear(); _assembler.Reset(); }
            _finished = false;
            _drained = false;
            _drainedAt = 0f;
            _playing = null;
            _nextUrl = null;
            _nextToken = null;
            FirstAudioMs = -1f;
        }

        /// <summary>
        /// Ends playback on the main thread. The reader callback runs on the AUDIO thread, where calling into
        /// UnityEngine is not allowed: a Stop() from there is at best ignored, which leaves the clip running to its
        /// full length with whatever the mixer still holds.
        /// </summary>
        private void Update()
        {
            if (_drained)
            {
                // Let the mixer play out what it already holds before stopping, or every answer loses its last
                // syllable. OnRead is returning silence by now, so the grace adds nothing audible.
                if (_drainedAt <= 0f) _drainedAt = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _drainedAt < tailGraceSeconds) return;
                _drained = false;
                _drainedAt = 0f;
                _playing = null;
                if (_source != null && _source.isPlaying) _source.Stop();
            }

            if (_playing != null || _nextUrl == null) return;
            var url = _nextUrl;
            var token = _nextToken;
            _nextUrl = null;
            _nextToken = null;
            Begin(url, token);
        }

        private IEnumerator Stream(string url, string bearerToken)
        {
            float startedAt = Time.realtimeSinceStartup;
            using var request = UnityWebRequest.Get(url);
            var handler = new PcmDownloadHandler(this);
            request.downloadHandler = handler;
            if (!string.IsNullOrEmpty(bearerToken)) request.SetRequestHeader("Authorization", "Bearer " + bearerToken);

            var operation = request.SendWebRequest();

            // Start the clip as soon as anything has arrived, not when the whole response has.
            while (!operation.isDone && FirstAudioMs < 0f) yield return null;
            if (FirstAudioMs < 0f && request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[Copilot] no answer audio: {request.error}. Showing the text only.");
                yield break;
            }
            FirstAudioMs = (Time.realtimeSinceStartup - startedAt) * 1000f;

            _source.clip = AudioClip.Create("copilot_answer", sampleRate * 60, 1, sampleRate, true, OnRead);
            _source.loop = false;
            _source.Play();

            yield return operation;
            _finished = true;
        }

        /// <summary>Called on the audio thread. Silence while the network is behind, so playback never crackles out.</summary>
        private void OnRead(float[] data)
        {
            lock (_lock)
            {
                for (int i = 0; i < data.Length; i++) data[i] = _pending.Count > 0 ? _pending.Dequeue() : 0f;
                if (_finished && _pending.Count == 0) _drained = true;   // Update() stops it: this is the audio thread
            }
        }

        internal void Enqueue(byte[] bytes, int count)
        {
            if (count <= 0) return;
            lock (_lock)
            {
                _samples.Clear();
                _assembler.Feed(bytes, count, _samples);          // a sample can straddle two chunks; PcmAssembler carries it
                foreach (var sample in _samples) _pending.Enqueue(sample);
            }
            if (FirstAudioMs < 0f) FirstAudioMs = 0f; // set properly by the coroutine; this just unblocks it
        }

        /// <summary>Hands every chunk straight to the player instead of buffering the whole response.</summary>
        private class PcmDownloadHandler : DownloadHandlerScript
        {
            private readonly PcmStreamPlayer _player;
            public PcmDownloadHandler(PcmStreamPlayer player) : base(new byte[16384]) => _player = player;

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength == 0) return false;
                _player.Enqueue(data, dataLength);
                return true;
            }
        }
    }
}
