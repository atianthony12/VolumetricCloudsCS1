using System;

namespace VolumetricClouds.UI
{
    /// <summary>
    /// Which one-time message a player is shown the first time the panel opens (2026-11-18, UI.NewDefaultsNotice;
    /// the welcome since 1.6.1). Pure (tools\test-quality.ps1 checks it).
    /// </summary>
    /// <remarks>
    /// The settings file carries &lt;ModVersion&gt;: the mod's version that last ran with it and had its news
    /// shown. A file without it was written before 1.6.0 -- an existing player. The notice comes back only
    /// when a later version moves the defaults again: raise <see cref="DefaultsChangedIn"/> to that
    /// version then (not every release).
    /// A NEW install (1.6.1, the author: "For users installing the mod for the first time, we can show this same
    /// modal ... Like a welcome modal basically. Again only shows the first time they open the panel") is stamped
    /// <see cref="NewInstall"/> until the welcome is answered, so it is still shown when the panel is first opened in
    /// a later session. (Until 1.6.1 a new install was stamped with the version at once and shown nothing.) An older
    /// version reads that stamp as no version: it would show its own notice once, harmless.
    /// </remarks>
    public static class NoticeRule
    {
        /// <summary>The version whose defaults the notice is about: a file stamped older than it is shown it once.</summary>
        // (1.6.1: the author's "New Defaults 1.6.1" profile moved four rows: a file stamped 1.6.0 is offered them once.)
        public const string DefaultsChangedIn = "1.6.1";

        /// <summary>The stamp of a new install that has not answered the welcome yet. Never a version.</summary>
        public const string NewInstall = "new install";

        public enum Kind
        {
            /// <summary>Nothing to show.</summary>
            None,

            /// <summary>An existing player, from before the defaults moved: offered the new defaults.</summary>
            Existing,

            /// <summary>A new install: the welcome, with the old defaults offered for a slower computer.</summary>
            Welcome,
        }

        /// <summary>
        /// What a settings file that was there at startup (<paramref name="fileExisted"/>; false: made just now
        /// for a new install), stamped <paramref name="stamped"/> (empty: before stamps), should be shown.
        /// </summary>
        public static Kind Decide(bool fileExisted, string stamped)
        {
            if (!fileExisted)
                return Kind.Welcome;

            if (stamped != null && string.Equals(stamped.Trim(), NewInstall, StringComparison.OrdinalIgnoreCase))
                return Kind.Welcome;

            Version seen = Parse(stamped);
            return seen == null || seen < Parse(DefaultsChangedIn) ? Kind.Existing : Kind.None;
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
