using System.Collections.Generic;

namespace CutOnce.Copilot.Voice
{
    /// <summary>
    /// Turns a stream of bytes into 16-bit samples, across chunk boundaries.
    ///
    /// The answer arrives as headerless little-endian PCM over a chunked response, and a download handler is handed
    /// whatever the socket had: over Wi-Fi that is an odd number of bytes about half the time. A sample is two bytes,
    /// so one of them can straddle two chunks. Dropping that stray byte — which is what happens if each chunk is read
    /// on its own — shifts every later sample by one byte: the low half of one sample pairs with the high half of the
    /// next, and the voice becomes white noise. Measured against a real answer split into MTU-sized pieces, the mean
    /// sample error was 9640 against speech whose own mean amplitude is 3299. Carrying the byte makes it exact.
    /// </summary>
    public class PcmAssembler
    {
        byte _carry;
        bool _hasCarry;

        public void Reset() => _hasCarry = false;

        /// <summary>Appends the samples in <paramref name="count"/> bytes of <paramref name="bytes"/> to <paramref name="into"/>.</summary>
        public void Feed(byte[] bytes, int count, ICollection<float> into)
        {
            if (bytes == null || count <= 0) return;
            var i = 0;
            if (_hasCarry)
            {
                into.Add(Sample(_carry, bytes[0]));
                _hasCarry = false;
                i = 1;
            }
            for (; i + 1 < count; i += 2) into.Add(Sample(bytes[i], bytes[i + 1]));
            if (i < count)
            {
                _carry = bytes[i];
                _hasCarry = true;
            }
        }

        static float Sample(byte low, byte high) => (short)(low | (high << 8)) / 32768f;
    }
}
