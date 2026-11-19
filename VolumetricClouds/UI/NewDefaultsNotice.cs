using System;
using System.IO;
using ColossalFramework.UI;
using ModsLibCS1.UI;
using UnityEngine;

namespace VolumetricClouds.UI
{
    /// <summary>
    /// The one-time notice of the new mod defaults for EXISTING players (2026-11-18, the author: "For
    /// existing users, let's show a one-time popup, alerting them of the new mod defaults ... The
    /// modal should be ours, made in ModsLibCS1 with the panel look that the user chose").
    /// </summary>
    /// <remarks>
    /// Decided at startup from the settings file's &lt;ModVersion&gt; (NoticeRule); shown the first time the
    /// player opens the panel in a city (never on its own at a load: the author's call, 2026-11-19), in the
    /// picked panel look (ModsLibCS1 Dialog). Either answer -- the close button and Escape are "No
    /// thanks" -- stamps the file with this version, so it is never shown again for these defaults. A
    /// city closed while it is open leaves it unanswered: it comes back when the panel is next opened.
    ///
    /// "Try new defaults" is "(Mod defaults)" in the profile bar (Profiles.SelectDefaults: the sky only;
    /// the quality preset, the Now tab and the computer's rows stay, the author's call). With unsaved
    /// changes it never throws them away: a second dialog saves them first -- into the picked profile, or,
    /// with none picked (or the picked one unreadable, never written over from here), as a new profile
    /// named "Unsaved profile pre-&lt;version&gt;" (the author's name). A player who never touched the sky has
    /// the OLD defaults, which no longer count as kept: they get that profile, their way back. If the
    /// save fails nothing changes and the first question comes back.
    /// </remarks>
    internal static class NewDefaultsNotice
    {
        private static bool _pending;
        private static Dialog _open;

        /// <summary>
        /// When the settings file has been read at startup (SettingsXml): shown this session, or stamped
        /// now. <paramref name="fileExisted"/>: a file (or the old .cgs) was there, so an existing player.
        /// </summary>
        public static void AtStartup(bool fileExisted)
        {
            string stamped = Settings.ModVersion != null ? Settings.ModVersion.value : string.Empty;
            _pending = NoticeRule.ShouldShow(fileExisted, stamped);
            if (_pending)
            {
                Log.Msg("new defaults: the notice will show when the panel is first opened (the settings file is stamped '" + stamped +
                        "'; the defaults moved in " + NoticeRule.DefaultsChangedIn + ")");
                return;
            }

            Stamp();
        }

        /// <summary>
        /// From ModController.Update: shows it once the player has opened the panel (F4, the button or
        /// Unified UI) and no other modal is up. Never on its own at a city load (the author's call,
        /// 2026-11-19: only when the panel is first opened).
        /// </summary>
        public static void Tick(bool panelOpen)
        {
            if (!_pending || _open != null || !panelOpen)
                return;

            if (UIView.HasModalInput())
                return;

            ShowQuestion();
        }

        /// <summary>From ModController.OnDestroy: the city is going; an open dialog goes with it, unanswered.</summary>
        public static void CityClosing()
        {
            if (_open != null)
            {
                _open.Close();
                _open = null;
                Log.Msg("new defaults: the city closed with the notice open; it comes back when the panel is next opened");
            }
        }

        private static void ShowQuestion()
        {
            _open = Show(Localization.Get("NewDefaults.Text"),
                new[] { Localization.Get("NewDefaults.Keep"), Localization.Get("NewDefaults.Try") }, 1, answer =>
                {
                    _open = null;
                    if (answer == 1)
                        TryDefaults();
                    else
                        Answered("No thanks" + (answer == Dialog.Closed ? " (closed)" : "") + ": the sky is left as it is");
                });

            if (_open != null)
                Log.Msg("new defaults: notice shown (panel look " + PanelLook.Skin + ")");
            else
                _pending = false;   // no UI to show it in: tried again next session
        }

        private static void TryDefaults()
        {
            if (!Profiles.HasUnsavedChanges)
            {
                Switch("nothing unsaved");
                return;
            }

            string picked = Profiles.Selected;
            bool intoPicked = picked.Length > 0 && Profiles.State == Profiles.Status.Selected;
            string newName = intoPicked ? null : FreeName();
            string text = intoPicked
                ? Localization.Get("NewDefaults.SavePicked", picked)
                : Localization.Get("NewDefaults.SaveNew", newName);

            _open = Show(text, new[] { Localization.Get("NewDefaults.Back"), Localization.Get("NewDefaults.SaveAndSwitch") }, 1,
                answer =>
                {
                    _open = null;
                    if (answer != 1)
                    {
                        ShowQuestion();   // Back (or closed): the first question again
                        return;
                    }

                    if (intoPicked)
                    {
                        if (Profiles.SaveSelected())
                            Switch("'" + picked + "' saved first");
                        else
                            SaveFailed("'" + picked + "' could not be saved");
                    }
                    else
                    {
                        string error;
                        if (Profiles.Add(newName, out error))
                            Switch("the sky saved first as the new profile '" + newName + "'");
                        else
                            SaveFailed("the profile '" + newName + "' could not be made (" + error + ")");
                    }
                });
        }

        private static void Switch(string how)
        {
            if (Profiles.SelectDefaults())
            {
                Answered("Try new defaults: " + how + ", then the mod's defaults put in");
                return;
            }

            SaveFailed("the mod's defaults could not be put in");
        }

        private static void SaveFailed(string what)
        {
            Log.Warn("new defaults: " + what + "; nothing else changed, the question is asked again");
            _open = Show(Localization.Get("NewDefaults.SaveFailed"), new[] { Localization.Get("NewDefaults.Ok") }, 0, answer =>
            {
                _open = null;
                ShowQuestion();
            });
        }

        /// <summary>"Unsaved profile pre-1.6.0", or with " 2", " 3"... after it when a profile has that name.</summary>
        private static string FreeName()
        {
            string name = Localization.Get("NewDefaults.SavedName", Mod.Version);
            if (ProfileName.Why(name) != null)
                name = "Unsaved profile pre-" + Mod.Version;

            string candidate = name;
            for (int n = 2; n < 100 && File.Exists(Profiles.PathOf(candidate)); n++)
                candidate = name + " " + n;
            return candidate;
        }

        private static void Answered(string what)
        {
            _pending = false;
            Stamp();
            SettingsXml.SaveNow();
            Log.Msg("new defaults: " + what + "; the file is stamped " + Mod.Version + ", the notice will not show again");
        }

        private static void Stamp()
        {
            if (Settings.ModVersion != null && Settings.ModVersion.value != Mod.Version)
                Settings.ModVersion.value = Mod.Version;
        }

        private static Dialog Show(string text, string[] buttons, int accent, Action<int> onAnswer)
        {
            try
            {
                return Dialog.Show(PanelLook.NewBuilder(), Mod.DisplayName, text, buttons, accent, onAnswer);
            }
            catch (Exception e)
            {
                Log.Error("new defaults: could not show the notice", e);
                return null;
            }
        }
    }
}
