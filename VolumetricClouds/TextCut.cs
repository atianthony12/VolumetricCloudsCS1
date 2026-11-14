using System.Text;

namespace VolumetricClouds
{
    /// <summary>
    /// Cutting outside text -- a profile's name, what a player typed or pasted -- for a label
    /// without ever leaving half a character in it.
    /// </summary>
    /// <remarks>
    /// A character outside the Basic Multilingual Plane (an emoji, a rare CJK character) is two
    /// UTF-16 units, a high half then a low half. A string that holds one half alone makes the
    /// game's font throw ("invalid utf-16 sequence") when it is asked to draw it, and in this game
    /// one such label stops every new text from drawing until a restart. So a cut never lands
    /// between the halves, and text that already holds a lone half shows a '?' in its place.
    /// Pure -- System only -- so tools/test-profiles.ps1 checks it on the built DLL.
    /// </remarks>
    internal static class TextCut
    {
        /// <summary>
        /// The first <paramref name="count"/> units of <paramref name="text"/> (all of it when it
        /// is shorter; "" for null or a count of 0 or less), one fewer when the cut would fall
        /// right after the first half of a pair.
        /// </summary>
        public static string First(string text, int count)
        {
            if (text == null || count <= 0)
                return string.Empty;

            if (text.Length <= count)
                return text;

            int n = count;
            if (char.IsHighSurrogate(text[n - 1]))
                n--;

            return text.Substring(0, n);
        }

        /// <summary>True when the text holds half a pair on its own; false for null or "".</summary>
        public static bool HasLoneSurrogate(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            for (int i = 0; i < text.Length; i++)
            {
                if (IsLone(text, i))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The text with every half pair on its own replaced by '?': the same string when there is
        /// none, "" for null.
        /// </summary>
        public static string Whole(string text)
        {
            if (text == null)
                return string.Empty;

            if (!HasLoneSurrogate(text))
                return text;

            var kept = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
                kept.Append(IsLone(text, i) ? '?' : text[i]);

            return kept.ToString();
        }

        /// <summary>A first half not followed by a second, or a second half not after a first.</summary>
        private static bool IsLone(string text, int i)
        {
            char c = text[i];

            if (char.IsHighSurrogate(c))
                return i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]);

            if (char.IsLowSurrogate(c))
                return i == 0 || !char.IsHighSurrogate(text[i - 1]);

            return false;
        }
    }
}
