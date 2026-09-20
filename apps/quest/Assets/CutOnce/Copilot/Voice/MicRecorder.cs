using System;
using UnityEngine;

namespace CutOnce.Copilot.Voice
{
    /// <summary>
    /// Push-to-talk recording, 16 kHz mono, encoded as the WAV the server's transcription expects.
    /// Push-to-talk is P0 and "Hey copilot" is P3 for one reason: a judging room is loud, and a wake
    /// word that fires on someone else's sentence loses the demo.
    /// </summary>
    public class MicRecorder : MonoBehaviour
    {
        public const int SampleRate = 16000;
        /// <summary>Section 10 caps a question at 12 s; the clip is allocated for that and trimmed on stop.</summary>
        public const int MaxSeconds = 12;

        private AudioClip _clip;
        private string _device;
        private float _startedAt;

        public bool IsRecording { get; private set; }
        public float ElapsedSeconds => IsRecording ? Time.realtimeSinceStartup - _startedAt : 0f;

        public bool Begin()
        {
            if (IsRecording) return true;
            if (Microphone.devices.Length == 0)
            {
                Debug.LogError("[Copilot] no microphone. Check the RECORD_AUDIO permission.");
                return false;
            }
            _device = Microphone.devices[0];
            _clip = Microphone.Start(_device, false, MaxSeconds, SampleRate);
            if (_clip == null)
            {
                Debug.LogError("[Copilot] the microphone could not start. Check the RECORD_AUDIO permission.");
                return false;
            }
            _startedAt = Time.realtimeSinceStartup;
            IsRecording = true;
            return true;
        }

        /// <summary>Stops and returns a WAV, or null if nothing usable was captured.</summary>
        public byte[] End()
        {
            if (!IsRecording) return null;
            int written = Microphone.GetPosition(_device);
            Microphone.End(_device);
            IsRecording = false;
            if (_clip == null || written <= 0) return null;

            int channels = _clip.channels;
            var samples = new float[written * channels];
            _clip.GetData(samples, 0);
            Destroy(_clip);
            _clip = null;
            if (channels <= 1) return EncodeWav(samples, SampleRate, 1);

            // Speech recognition expects mono. Some Windows/Link microphones expose two channels; declaring their
            // interleaved samples as mono doubles the apparent duration and can turn speech into rhythmic noise.
            var mono = new float[written];
            for (int frame = 0; frame < written; frame++)
            {
                float sum = 0f;
                for (int channel = 0; channel < channels; channel++) sum += samples[frame * channels + channel];
                mono[frame] = sum / channels;
            }
            return EncodeWav(mono, SampleRate, 1);
        }

        public void Cancel()
        {
            if (!IsRecording) return;
            Microphone.End(_device);
            IsRecording = false;
            if (_clip != null) { Destroy(_clip); _clip = null; }
        }

        /// <summary>16-bit PCM in a 44-byte RIFF header. Written by hand so there is no package to add.</summary>
        public static byte[] EncodeWav(float[] samples, int sampleRate, int channels)
        {
            int dataBytes = samples.Length * 2;
            var wav = new byte[44 + dataBytes];
            void PutAscii(int at, string s) { for (int i = 0; i < s.Length; i++) wav[at + i] = (byte)s[i]; }
            void PutInt(int at, int v) { BitConverter.GetBytes(v).CopyTo(wav, at); }
            void PutShort(int at, short v) { BitConverter.GetBytes(v).CopyTo(wav, at); }

            PutAscii(0, "RIFF"); PutInt(4, 36 + dataBytes); PutAscii(8, "WAVE");
            PutAscii(12, "fmt "); PutInt(16, 16); PutShort(20, 1); PutShort(22, (short)channels);
            PutInt(24, sampleRate); PutInt(28, sampleRate * channels * 2);
            PutShort(32, (short)(channels * 2)); PutShort(34, 16);
            PutAscii(36, "data"); PutInt(40, dataBytes);

            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)(Mathf.Clamp(samples[i], -1f, 1f) * short.MaxValue);
                wav[44 + i * 2] = (byte)(s & 0xff);
                wav[45 + i * 2] = (byte)((s >> 8) & 0xff);
            }
            return wav;
        }
    }
}
