using System;

namespace VolumetricClouds.UI
{
    /// <summary>
    /// Whether an existing player is shown the new-defaults notice (2026-11-18, UI.NewDefaultsNotice).
    /// Pure (tools\test-notice.ps1 compiles it).
    /// </summary>
    /// <remarks>
    /// The settings file carries &lt;ModVersion&gt;: the mod's version that last ran with it and had its news
    /// shown. A file without it was written before 1.6.0 -- an existing player. A new install gets the
    /// version at once and nothing is shown (its sky IS the new defaults). The notice comes back only
    /// when a later version moves the defaults again: raise <see cref="DefaultsChangedIn"/> to that
    /// version then (not every release).
    /// </remarks>
    public static class NoticeRule
    {
        /// <summary>The version whose defaults the notice is about: a file stamped older than it is shown it once.</summary>
        public const string DefaultsChangedIn = "1.6.0";

        /// <summary>
        /// True when a settings file that was there at startup (<paramref name="fileExisted"/>), stamped
        /// <paramref name="stamped"/> (empty: before stamps), should be shown the notice.
        /// </summary>
        public static bool ShouldShow(bool fileExisted, string stamped)
        {
            if (!fileExisted)
                return false;

            Version seen = Parse(stamped);
            return seen == null || seen < Parse(DefaultsChangedIn);
        }

        /// <summary>A version as written ("1.6.0"); null for empty or unreadable text (read as "before stamps").</summary>
        public static Version Parse(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            try
            {
                return new Version(text.Trim());
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
