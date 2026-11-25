using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.UI;
using ModsLibCS1;
using UnityEngine;

namespace VolumetricClouds
{
    /// <summary>
    /// The game-update safeguard (the author's call, 2026-11-24, for every one of his mods: a mod
    /// checks what it needs of the game and turns itself off rather than run half-patched), on
    /// ModsLibCS1's GameCheck: at Mod.OnEnabled, before anything of the mod starts,
    /// every game member it patches or reads by name is looked for; Patcher applies each patch class
    /// through the same check. All or nothing.
    /// </summary>
    /// <remarks>
    /// OFF MEANS NOTHING RUNS (no keeper, unlike Flag Maker: a city without this mod loses nothing):
    /// no patch, no controller, button, panel or keys (Loader), the options page one line
    /// (OptionsUI), the settings and profiles read but never written (SettingsXml.ReadOnly), the
    /// city's saved sky neither read nor written (SkySaveData: the game carries a key no extension
    /// writes, so a city saved while off keeps it for the fixed version), and the APIs other mods
    /// read (Sky.WeatherWind, Sky.LightningBolts, Lighting.LampFlare) answer as if this mod were not
    /// there, so each falls back to its own.
    /// </remarks>
    public static class GameUpdate
    {
        private static GameCheck _check;

        /// <summary>The mod has switched itself off (the game changed under it): nothing of it runs.</summary>
        public static bool Off { get; private set; }

        /// <summary>The check, for Patcher (null before <see cref="Check"/>).</summary>
        public static GameCheck Current
        {
            get { return _check; }
        }

        /// <summary>
        /// Each Harmony target <see cref="Require"/> found, in the order it checked them: the offline
        /// test compares it with every [HarmonyPatch] class in the DLL, so a new patch cannot go
        /// unchecked.
        /// </summary>
        public static readonly List<MethodInfo> HarmonyTargets = new List<MethodInfo>();

        /// <summary>
        /// Mod.OnEnabled, first: the members the mod reads by name and the Harmony targets, looked for
        /// now, whatever Harmony does later. A throw (a type gone: the JIT of <see cref="Require"/>
        /// fails) is a failure too.
        /// </summary>
        public static void Check()
        {
            if (_check != null)
                return;

            _check = new GameCheck(Mod.DisplayName, line => Log.Error(line));
            try
            {
                Require(_check);
            }
            catch (Exception e)
            {
                _check.Problem("the check itself threw: " + e.GetType().Name + ": " + e.Message);
            }

            if (_check.Failed)
                SwitchOff();
            else
                Log.Msg("game check: the game (" + GameCheck.GameVersion + ") has everything " + Mod.DisplayName + " patches and reads by name");
        }

        /// <summary>
        /// What the mod needs of the game by name. The plan's rule: a Harmony target (its signature,
        /// and the parameter names its patch binds by) and every game member read by name. Not counted,
        /// each with its own fallback: another library's members (UUIIntegration's look for Unified UI's
        /// panel), and what only feeds the log (Lighting.LightingProbe's light-system fields). Public
        /// for the offline test, which runs it against the game's DLLs.
        /// </summary>
        public static void Require(GameCheck check)
        {
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags declared = instance | BindingFlags.DeclaredOnly;
            const BindingFlags declaredStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            HarmonyTargets.Clear();

            // ---- Harmony targets (Lighting/HaloPatches.cs, Sky/RainPatches.cs) ----
            // GameCheck reads parameter names by POSITION: a list runs up to the last name a patch
            // binds (DrawLight's spotLeaking is checked though the prefix skips it).

            // DrawLightParametersPatch: its prefix binds type, pos, dir, vel, color, intensity, range,
            // spotAngle, volume (vel by ref: the patch's choice; the game's is by value).
            Target(check.RequireMethod(typeof(LightSystem), "DrawLight", declared,
                new[] { typeof(LightType), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(Color),
                        typeof(float), typeof(float), typeof(float), typeof(float), typeof(bool) },
                "type", "pos", "dir", "vel", "color", "intensity", "range", "spotAngle", "spotLeaking", "volume"));

            // LampRenderEffectPatch: __instance and id (LightEffect's own override).
            Target(check.RequireMethod(typeof(LightEffect), "RenderEffect", declared,
                new[] { typeof(InstanceID), typeof(EffectInfo.SpawnArea), typeof(Vector3), typeof(float),
                        typeof(float), typeof(float), typeof(float), typeof(RenderManager.CameraInfo) },
                "id"));

            // VehicleRenderPatch: the static 16-argument overload, nothing bound.
            Target(check.RequireMethod(typeof(Vehicle), "RenderInstance", declaredStatic,
                new[] { typeof(RenderManager.CameraInfo), typeof(VehicleInfo), typeof(Vector3), typeof(Quaternion), typeof(Vector3),
                        typeof(Vector4), typeof(Vector4), typeof(Vector3), typeof(float), typeof(Color), typeof(Vehicle.Flags),
                        typeof(Vehicle.Flags2), typeof(int), typeof(InstanceID), typeof(bool), typeof(bool) }));

            // DrawLightDataPatch: nothing bound.
            Target(check.RequireMethod(typeof(LightSystem), "DrawLight", declared, new[] { typeof(LightData) }));

            // SampleRainIntensityPatch: pos, ignoreWeather, and ref float __result (the return type).
            MethodInfo rain = check.RequireMethod(typeof(WeatherManager), "SampleRainIntensity", declared,
                new[] { typeof(Vector3), typeof(bool) }, "pos", "ignoreWeather");
            if (rain != null && rain.ReturnType != typeof(float))
                check.Problem("WeatherManager.SampleRainIntensity returns a " + rain.ReturnType.FullName + " now, not a float");
            Target(rain);

            // ---- game members read by name ----

            // Sky/CloudLightning.Queue: the game's own strikes, taken over by the clouds.
            check.RequireField(typeof(WeatherManager), "m_lightningQueue", instance, typeof(FastList<WeatherManager.LightningStrike>));

            // Sky/GameRain.Copies: the snowflakes' three material copies, scaled to the clouds.
            check.RequireField(typeof(RainParticleProperties), "m_RainMaterialCopyX", instance, typeof(Material));
            check.RequireField(typeof(RainParticleProperties), "m_RainMaterialCopyY", instance, typeof(Material));
            check.RequireField(typeof(RainParticleProperties), "m_RainMaterialCopyZ", instance, typeof(Material));

            // Sky/GameStars: the stars' material, moved behind the clouds (a property: its getter).
            MethodInfo stars = check.RequireMethod(typeof(DayNightProperties), "get_starMaterial", instance, new Type[0]);
            if (stars != null && stars.ReturnType != typeof(Material))
                check.Problem("DayNightProperties.starMaterial is a " + stars.ReturnType.FullName + " now, not a Material");

            // UI/OptionsUI.RebuildNextFrame: the options page rebuilt in a newly picked language.
            check.RequireMethod(typeof(OptionsMainPanel), "OnLocaleChanged", BindingFlags.Instance | BindingFlags.NonPublic, new Type[0]);

            check.ForcedFailure();
        }

        private static void Target(MethodInfo method)
        {
            if (method != null)
                HarmonyTargets.Add(method);
        }

        /// <summary>
        /// A patch class that would not apply (Patcher, when Harmony is ready): the check has failed
        /// late, the patches are undone, and the mod switches off as at start-up.
        /// </summary>
        public static void PatchFailed()
        {
            if (Off)
                return;

            SwitchOff();
            Announce();

            // Harmony was ready only after a city had loaded (the mod enabled mid-game): its sky goes too.
            Loader.StandDownForGameUpdate();
        }

        private static void SwitchOff()
        {
            Off = true;

            // Nothing written over the player's settings or profiles: they wait for the fixed version.
            SettingsXml.ReadOnly = true;

            _check.Summary(Mod.Version);
            Log.Error(Mod.DisplayName + " is off until it is updated: nothing of it runs, and its settings, profiles and " +
                      "the cities' saved skies are left as they are");
        }

        /// <summary>
        /// The pop-up, once a launch (shown at the main menu). Mod.OnEnabled calls it after
        /// Settings.Init, so the text is in the player's chosen language.
        /// </summary>
        public static void Announce()
        {
            if (!Off || _check == null)
                return;

            string title = Localization.Get("GameUpdate.Title", Mod.DisplayName);
            string text = Localization.Get("GameUpdate.Text");
            _check.ShowOnce(title, text);
        }
    }
}
