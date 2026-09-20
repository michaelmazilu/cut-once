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

        private readonly Queue<float> _pending = new Queue<float>();
        private readonly PcmAssembler _assembler = new PcmAssembler();
        private readonly List<float> _samples = new List<float>(8192);
        private readonly object _lock = new object();
        private AudioSource _source;
        private bool _finished;
        private string _playing;
        /// <summary>Set on the audio thread when the last sample has been handed over; acted on in Update, where Unity's API is safe to call.</summary>
        private volatile bool _drained;

        public bool IsPlaying => _source != null && _source.isPlaying;
        /// <summary>Milliseconds from the request going out to the first sample being queued. This is the G6 number.</summary>
        public float FirstAudioMs { get; private set; } = -1f;

        private void Awake() => _source = GetComponent<AudioSource>();

        public void Play(string baseUrl, string audioPath, string bearerToken)
        {
            var url = $"{baseUrl.TrimEnd('/')}{audioPath}";
            // Asked for the same answer again while it is still being said: let it finish. Playing it a second time
            // over the first is how one sentence ends up repeating.
            if (IsPlaying && url == _playing) { Debug.Log($"[Copilot] already saying {audioPath}; ignoring the repeat request."); return; }
            Stop();
            _playing = url;
            StartCoroutine(Stream(url, bearerToken));
        }

        public void Stop()
        {
            StopAllCoroutines();
            if (_source != null) _source.Stop();
            lock (_lock) { _pending.Clear(); _assembler.Reset(); }
            _finished = false;
            _drained = false;
            _playing = null;
            FirstAudioMs = -1f;
        }

        /// <summary>
        /// Ends playback on the main thread. The reader callback runs on the AUDIO thread, where calling into
        /// UnityEngine is not allowed: a Stop() from there is at best ignored, which leaves the clip running to its
        /// full length with whatever the mixer still holds.
        /// </summary>
        private void Update()
        {
            if (!_drained) return;
            _drained = false;
            _playing = null;
            if (_source != null && _source.isPlaying) _source.Stop();
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
