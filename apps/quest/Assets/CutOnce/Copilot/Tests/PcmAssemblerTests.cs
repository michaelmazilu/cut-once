using System;
using System.Collections.Generic;
using CutOnce.Copilot.Voice;
using NUnit.Framework;

namespace CutOnce.Copilot.Tests
{
    /// <summary>
    /// The answer's bytes arrive in whatever pieces the socket had. This is the test that says a sample split across
    /// two of them still comes out as that sample: read chunk by chunk, dropping the stray byte, the voice turns into
    /// static, which is what a headset actually did.
    /// </summary>
    public class PcmAssemblerTests
    {
        static byte[] Pcm(short[] samples)
        {
            var bytes = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
            {
                bytes[i * 2] = (byte)(samples[i] & 0xff);
                bytes[i * 2 + 1] = (byte)((samples[i] >> 8) & 0xff);
            }
            return bytes;
        }

        [Test]
        public void ASampleSplitAcrossTwoChunksSurvivesIt()
        {
            var samples = new short[] { 0, 1000, -1000, short.MaxValue, short.MinValue, 12345, -12345 };
            var bytes = Pcm(samples);
            var assembler = new PcmAssembler();
            var got = new List<float>();
            // Cut between the two bytes of the second sample, which is where a chunk boundary is free to land.
            assembler.Feed(bytes, 3, got);
            var rest = new byte[bytes.Length - 3];
            Array.Copy(bytes, 3, rest, 0, rest.Length);
            assembler.Feed(rest, rest.Length, got);

            Assert.That(got.Count, Is.EqualTo(samples.Length), "a sample was lost at the seam");
            for (var i = 0; i < samples.Length; i++)
                Assert.That(got[i], Is.EqualTo(samples[i] / 32768f).Within(1e-6f), $"sample {i} came back wrong");
        }

        [Test]
        public void ManyOddChunksInARowStayInStep()
        {
            var samples = new short[600];
            for (var i = 0; i < samples.Length; i++) samples[i] = (short)(Math.Sin(i * 0.05) * 20000);
            var bytes = Pcm(samples);
            var assembler = new PcmAssembler();
            var got = new List<float>();
            var random = new System.Random(7);
            for (var at = 0; at < bytes.Length;)
            {
                var take = Math.Min(random.Next(1, 40), bytes.Length - at);   // odd as often as even, as a socket is
                var chunk = new byte[take];
                Array.Copy(bytes, at, chunk, 0, take);
                assembler.Feed(chunk, take, got);
                at += take;
            }

            Assert.That(got.Count, Is.EqualTo(samples.Length));
            var worst = 0f;
            for (var i = 0; i < samples.Length; i++) worst = Math.Max(worst, Math.Abs(got[i] - samples[i] / 32768f));
            Assert.That(worst, Is.LessThan(1e-6f), "the stream drifted out of step: that is the static");
        }
    }
}
