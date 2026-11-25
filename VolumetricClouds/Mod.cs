using ICities;
using VolumetricClouds.UI;

namespace VolumetricClouds
{
    public class Mod : IUserMod
    {
        /// <summary>
        /// The mod's name wherever a player reads it: the mods list, the options page, the
        /// panel's title, button tooltips, dialog titles, the log's first line. Files, the
        /// assembly, the namespace and the button IDs stay "VolumetricClouds".
        /// </summary>
        public const string DisplayName = "Volumetric Weather";

        /// <summary>
        /// The release a player has: raised once per Workshop upload (the third number for a
        /// fix or a compatibility change such as a new platform, the second for new features),
        /// with its own section in CHANGELOG.md. Also the first three parts of the assembly
        /// version (Properties/AssemblyInfo.cs).
        /// </summary>
        public const string Version = "1.6.1";

        /// <summary>
        /// The mods list and the options page show the version after the name, so a bug report
        /// can say which release it was. The game reads Name for display only (mods list,
        /// options category, telemetry); whether a mod is enabled is keyed on its folder, so a
        /// new number each release changes nothing a player has saved.
        /// </summary>
        public string Name => DisplayName + " " + Version;
        public string Description => Localization.Get("Mod.Description");

        /// <summary>Called by the game via reflection when the mod is enabled.</summary>
        public void OnEnabled()
        {
            Log.Start();
            Log.Msg("Mod enabled. Log file: " + Log.Path);

            // Before anything of the mod starts: has the game everything it patches and reads by
            // name? If not it switches itself off and nothing of it runs (GameUpdate). Each step
            // below caught HERE: a compiled reference the game took away throws as the callee is
            // compiled, before its own try is entered.
            Step(GameUpdate.Check, "the game check");

            // Reads VolumetricClouds.xml (or imports the old .cgs, once) and starts the saver
            // that writes changes back and picks up edits made to the file by hand (read-only
            // while off: GameUpdate).
            Step(Settings.Init, "the settings");
            if (!GameUpdate.Off)
                Step(SettingsXml.StartSaver, "the settings' saver"); // again after a disable/enable, when Init has nothing left to do
            else
                Step(GameUpdate.Announce, "the switched-off pop-up"); // in the player's language, read with the settings

            // None while off.
            if (!GameUpdate.Off)
            {
                Step(Patcher.EnsureHarmony, "Harmony's check");
                Step(Patcher.PatchOnReady, "the patches");
            }
        }

        private static void Step(System.Action step, string what)
        {
            try
            {
                step();
            }
            catch (System.Exception e)
            {
                Log.Error("Mod enabled: " + what + " threw; the rest goes on", e);
            }
        }

        /// <summary>Called by the game via reflection when the mod is disabled.</summary>
        public void OnDisabled()
        {
            Patcher.Unpatch();
            SettingsXml.Shutdown();
        }

        /// <summary>
        /// Called by the game via reflection when the options page is (re)built -- at least
        /// once per scene, and again whenever any mod is enabled or disabled. Everything that
        /// is configured once lives here; the overrides and the live look are in the in-game
        /// panel, where they can be judged against the sky.
        /// </summary>
        public void OnSettingsUI(UIHelperBase helper)
        {
            OptionsUI.Build(helper);
        }
    }
}
