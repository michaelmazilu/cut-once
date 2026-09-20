namespace CutOnce.Core.Build
{
    /// <summary>Preparing written text to be said aloud.</summary>
    public static class SpokenText
    {
        /// <summary>
        /// Trims to something that can be SAID: the last sentence that fits, or failing that the last whole word.
        /// A bare Substring stops mid-word, and a voice stopping mid-word is heard as the voice being cut off
        /// rather than as an instruction that simply ran long.
        /// </summary>
        public static string Fit(string text, int maxCharacters)
        {
            if (string.IsNullOrEmpty(text) || maxCharacters <= 0 || text.Length <= maxCharacters) return text;

            var end = text.LastIndexOfAny(new[] { '.', '!', '?' }, maxCharacters - 1);
            // …but only when a sentence gets us most of the way there. "Ok." followed by the real instruction must
            // not become "Ok."; in that case a word break says far more of what was meant.
            if (end >= maxCharacters / 2) return text.Substring(0, end + 1);

            var word = text.LastIndexOf(' ', maxCharacters - 1);
            return word > 0 ? text.Substring(0, word).TrimEnd() : text.Substring(0, maxCharacters);
        }
    }
}
