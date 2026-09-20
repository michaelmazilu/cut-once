using CutOnce.Core.Build;
using NUnit.Framework;

namespace CutOnce.Core.Tests
{
    /// <summary>
    /// What reaches the voice when a step instruction runs long. It used to be Substring(0, 400), which stops
    /// mid-word — and a voice stopping mid-word is heard as the voice breaking, not as an instruction running long.
    /// </summary>
    public class SpokenTextTests
    {
        [Test]
        public void ShortTextIsLeftExactlyAlone()
        {
            const string step = "Fold the flap down and tape it.";
            Assert.That(SpokenText.Fit(step, 400), Is.EqualTo(step));
            Assert.That(SpokenText.Fit(null, 400), Is.Null);
            Assert.That(SpokenText.Fit("", 400), Is.Empty);
        }

        [Test]
        public void ItStopsAtTheLastWholeSentence()
        {
            var text = "Stand the big box upright. Tape the long seam shut. " + new string('x', 500);
            Assert.That(SpokenText.Fit(text, 80), Is.EqualTo("Stand the big box upright. Tape the long seam shut."));
        }

        [Test]
        public void WithNoSentenceEndItStopsBetweenWords()
        {
            // 28 lands inside "going" — a bare Substring would stop at "…and goi", which is the whole bug.
            const string text = "keep going and going and going and going and going and going and going";
            var said = SpokenText.Fit(text, 28);
            Assert.That(said.Length, Is.LessThanOrEqualTo(28));
            Assert.That(text.StartsWith(said), Is.True, "what is said must be a prefix of what was meant");
            Assert.That(text[said.Length], Is.EqualTo(' '), $"cut inside a word: \"{said}\"");
        }

        [Test]
        public void AnEarlyFullStopIsNotWorthStoppingAt()
        {
            // Stopping at the first full stop here would say "Ok." and drop the instruction entirely.
            const string text = "Ok. Now stand the long panel against the wall and hold it there while the glue sets.";
            var said = SpokenText.Fit(text, 40);
            Assert.That(said.Length, Is.GreaterThan(20), $"stopped far too early: \"{said}\"");
            Assert.That(text.StartsWith(said), Is.True);
        }

        [Test]
        public void OneUnbrokenWordIsStillCut()
        {
            // Nothing sensible to cut at. Better a hard cut than a sentence that never ends.
            Assert.That(SpokenText.Fit(new string('y', 100), 20).Length, Is.EqualTo(20));
        }
    }
}
