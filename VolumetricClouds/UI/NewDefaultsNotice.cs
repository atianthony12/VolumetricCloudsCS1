using System;
using System.IO;
using ColossalFramework.UI;
using ModsLibCS1.UI;
using UnityEngine;

namespace VolumetricClouds.UI
{
    /// <summary>
    /// The one-time message the first time the panel is opened: the new defaults for EXISTING players (2026-11-18,
    /// the author: "For existing users, let's show a one-time popup, alerting them of the new mod defaults ... The
    /// modal should be ours, made in ModsLibCS1 with the panel look that the user chose"), and since 1.6.1 a
    /// WELCOME for a new install ("Thanks for installing Volumetric Weather! ..."), which starts on the new
    /// defaults and is offered the old ones for a slower computer.
    /// </summary>
    /// <remarks>
    /// Decided at startup from the settings file's &lt;ModVersion&gt; (NoticeRule); shown the first time the
    /// player opens the panel in a city (never on its own at a load: the author's call, 2026-11-19), in the
    /// picked panel look (ModsLibCS1 Dialog). Either answer -- the close button and Escape are "No thanks" /
    /// "Keep new defaults" -- stamps the file with this version, so it is never shown again for these defaults.
    /// A city closed while it is open leaves it unanswered: it comes back when the panel is next opened.
    ///
    /// Both carry a paragraph in yellow on performance (1.6.1, the author: the new defaults cost some speed;
    /// "don't say it costs 'a lot of performance', we don't want to exaggerate and scare users away").
    ///
    /// "Try new defaults" picks the built-in "(new defaults)" profile, "Try old defaults" "(old defaults)"
    /// (Profiles: the sky only; the quality preset, the Now tab and the computer's rows stay, the author's
    /// call). With unsaved changes it never throws them away: a second dialog saves them first -- into the
    /// picked profile, or, with none picked (or a built-in one, which has no file, or the picked one
    /// unreadable, never written over from here), as a new profile named "Unsaved profile pre-&lt;version&gt;"
    /// (the author's name). If the save fails nothing changes and the first question comes back.
    /// </remarks>
    internal static class NewDefaultsNotice
    {
        private static NoticeRule.Kind _pending;
        private static Dialog _open;

        /// <summary>
        /// When the settings file has been read at startup (SettingsXml): shown this session, or stamped
        /// now. <paramref name="fileExisted"/>: a file (or the old .cgs) was there, so an existing player;
        /// false: a new install's file made just now.
        /// </summary>
        public static void AtStartup(bool fileExisted)
        {
            string stamped = Settings.ModVersion != null ? Settings.ModVersion.value : string.Empty;
            _pending = NoticeRule.Decide(fileExisted, stamped);
            if (_pending == NoticeRule.Kind.Welcome)
            {
                Log.Msg("new defaults: a new install -- the welcome will show when the panel is first opened");
                return;
            }

            if (_pending == NoticeRule.Kind.Existing)
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
            if (_pending == NoticeRule.Kind.None || _open != null || !panelOpen)
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
                Log.Msg("new defaults: the city closed with the message open; it comes back when the panel is next opened");
            }
        }

        private static void ShowQuestion()
        {
            bool welcome = _pending == NoticeRule.Kind.Welcome;
            if (welcome)
            {
                // "Try old defaults" in the plain look, "Keep new defaults" the accent (the author: "Try old defaults" in gray).
                _open = Show(new[]
                    {
                        new Dialog.Paragraph(Localization.Get("Welcome.Thanks")),
                        new Dialog.Paragraph(Localization.Get("Welcome.Performance"), Dialog.WarningYellow),
                        new Dialog.Paragraph(Localization.Get("Welcome.Change")),
                    },
                    new[] { Localization.Get("Welcome.TryOld"), Localization.Get("Welcome.Keep") }, 1, answer =>
                    {
                        _open = null;
                        if (answer == 0)
                            TryProfile(Profiles.OldDefaults, "Try old defaults");
                        else
                            Answered("Keep new defaults" + (answer == Dialog.Closed ? " (closed)" : "") + ": the sky is left as it is");
                    });
            }
            else
            {
                _open = Show(new[]
                    {
                        new Dialog.Paragraph(Localization.Get("NewDefaults.Text")),
                        new Dialog.Paragraph(Localization.Get("NewDefaults.Performance"), Dialog.WarningYellow),
                    },
                    new[] { Localization.Get("NewDefaults.Keep"), Localization.Get("NewDefaults.Try") }, 1, answer =>
                    {
                        _open = null;
                        if (answer == 1)
                            TryProfile(Profiles.NewDefaults, "Try new defaults");
                        else
                            Answered("No thanks" + (answer == Dialog.Closed ? " (closed)" : "") + ": the sky is left as it is");
                    });
            }

            if (_open != null)
                Log.Msg("new defaults: " + (welcome ? "welcome" : "notice") + " shown (panel look " + PanelLook.Skin + ")");
            else
                _pending = NoticeRule.Kind.None;   // no UI to show it in: tried again next session
        }

        /// <summary>Picks a built-in profile, saving the sky first when it has changes kept nowhere else.</summary>
        private static void TryProfile(string builtIn, string answer)
        {
            if (!Profiles.HasUnsavedChanges)
            {
                Switch(builtIn, answer, "nothing unsaved");
                return;
            }

            string picked = Profiles.Selected;
            bool intoPicked = picked.Length > 0 && Profiles.State == Profiles.Status.Selected && !Profiles.BuiltInPicked;
            string newName = intoPicked ? null : FreeName();
            string shown = Profiles.Shown(builtIn);
            string text = intoPicked
                ? Localization.Get("NewDefaults.SavePicked", picked, shown)
                : Localization.Get("NewDefaults.SaveNew", newName, shown);

            _open = Show(new[] { new Dialog.Paragraph(text) },
                new[] { Localization.Get("NewDefaults.Back"), Localization.Get("NewDefaults.SaveAndSwitch") }, 1,
                reply =>
                {
                    _open = null;
                    if (reply != 1)
                    {
                        ShowQuestion();   // Back (or closed): the first question again
                        return;
                    }

                    if (intoPicked)
                    {
                        if (Profiles.SaveSelected())
                            Switch(builtIn, answer, "'" + picked + "' saved first");
                        else
                            SaveFailed("'" + picked + "' could not be saved");
                    }
                    else
                    {
                        string error;
                        if (Profiles.Add(newName, out error))
                            Switch(builtIn, answer, "the sky saved first as the new profile '" + newName + "'");
                        else
                            SaveFailed("the profile '" + newName + "' could not be made (" + error + ")");
                    }
                });
        }

        private static void Switch(string builtIn, string answer, string how)
        {
            if (Profiles.Select(builtIn))
            {
                Answered(answer + ": " + how + ", then " + builtIn + " picked");
                return;
            }

            SaveFailed(builtIn + " could not be put in");
        }

        private static void SaveFailed(string what)
        {
            Log.Warn("new defaults: " + what + "; nothing else changed, the question is asked again");
            _open = Show(new[] { new Dialog.Paragraph(Localization.Get("NewDefaults.SaveFailed")) },
                new[] { Localization.Get("NewDefaults.Ok") }, 0, answer =>
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
            _pending = NoticeRule.Kind.None;
            Stamp();
            SettingsXml.SaveNow();
            Log.Msg("new defaults: " + what + "; the file is stamped " + Mod.Version + ", the message will not show again");
        }

        private static void Stamp()
        {
            if (Settings.ModVersion != null && Settings.ModVersion.value != Mod.Version)
                Settings.ModVersion.value = Mod.Version;
        }

        private static Dialog Show(Dialog.Paragraph[] paragraphs, string[] buttons, int accent, Action<int> onAnswer)
        {
            try
            {
                return Dialog.Show(PanelLook.NewBuilder(), Mod.DisplayName, paragraphs, buttons, accent, onAnswer);
            }
            catch (Exception e)
            {
                Log.Error("new defaults: could not show the message", e);
                return null;
            }
        }
    }
}
