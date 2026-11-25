using System;
using System.Reflection;
using CitiesHarmony.API;
using HarmonyLib;
using UnityEngine;

namespace VolumetricClouds
{
    /// <summary>
    /// Harmony bootstrap. The CitiesHarmony mod provides the actual Harmony assembly at
    /// runtime, so patching is deferred until it reports ready rather than assumed.
    /// </summary>
    public static class Patcher
    {
        private const string HarmonyId = "volumetricclouds.lighting";

        private static bool _patched;

        public static bool IsPatched => _patched;

        public static void PatchOnReady()
        {
            try
            {
                HarmonyHelper.DoOnHarmonyReady(Patch);
            }
            catch (Exception e)
            {
                Log.Error("Could not schedule Harmony patching.", e);
            }
        }

        /// <summary>Prompts the player to subscribe if the Harmony mod is missing.</summary>
        public static void EnsureHarmony()
        {
            try
            {
                if (!HarmonyHelper.IsHarmonyInstalled)
                {
                    Log.Warn("Harmony is NOT installed; light halo options will not work.");
                    HarmonyHelper.EnsureHarmonyInstalled();
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }

        private static void Patch()
        {
            // Switched off by the game check (GameUpdate): nothing of ours is patched.
            if (_patched || GameUpdate.Off)
                return;

            try
            {
                Harmony harmony = new Harmony(HarmonyId);

                // Each class on its own, through the game check (GameUpdate), in place of PatchAll
                // (which stops at the first target it cannot find and leaves the ones before it on).
                // One that will not apply is the game changed under the mod: every patch of ours is
                // undone and the mod switches itself off -- never left half-patched. No transpilers.
                Type[] patchClasses =
                {
                    typeof(Lighting.DrawLightParametersPatch), typeof(Lighting.LampRenderEffectPatch),
                    typeof(Lighting.VehicleRenderPatch), typeof(Lighting.DrawLightDataPatch),
                    typeof(Sky.SampleRainIntensityPatch),
                };
                ModsLibCS1.GameCheck check = GameUpdate.Current;
                foreach (Type patch in patchClasses)
                {
                    Type captured = patch;
                    if (check != null)
                        check.Patch(patch.Name, () => harmony.CreateClassProcessor(captured).Patch());
                    else
                        harmony.CreateClassProcessor(captured).Patch(); // never: GameUpdate.Check runs first in OnEnabled
                }

                if (check != null && check.UndoIfFailed(() => harmony.UnpatchAll(HarmonyId)))
                {
                    Log.Msg("Harmony patches removed (" + HarmonyId + "): the game check failed");
                    GameUpdate.PatchFailed();
                    return;
                }

                _patched = true;
                Log.Msg("Harmony patches applied (" + HarmonyId + "). Harmony installed=" + HarmonyHelper.IsHarmonyInstalled);
            }
            catch (Exception e)
            {
                Log.Error("Harmony patching failed; light halo options disabled.", e);
            }
        }

        /// <summary>
        /// One line naming every other Harmony ID that patches a method we patch, written with
        /// the system report. A clash on one of these methods is the likeliest way another mod
        /// and ours break each other, and without this line a report cannot say which mod.
        /// Only Harmony patches are visible here: an old mod that detours a method by hand
        /// does not show up.
        /// </summary>
        public static void LogSharedMethods()
        {
            if (!_patched)
                return;

            try
            {
                string shared = "";

                foreach (MethodBase method in new Harmony(HarmonyId).GetPatchedMethods())
                {
                    Patches info = Harmony.GetPatchInfo(method);
                    if (info == null)
                        continue;

                    string others = "";
                    foreach (string owner in info.Owners)
                    {
                        if (owner != HarmonyId)
                            others += (others.Length > 0 ? ", " : "") + owner;
                    }

                    if (others.Length > 0)
                        shared += (shared.Length > 0 ? "; " : "") + method.DeclaringType.Name + "." + method.Name + " by " + others;
                }

                Log.Msg("harmony: other mods patching the methods we patch: " + (shared.Length > 0 ? shared : "none"));
            }
            catch (Exception e)
            {
                Log.Warn("Could not list other mods' patches: " + e.Message);
            }
        }

        public static void Unpatch()
        {
            if (!_patched)
                return;

            try
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                Log.Msg("Harmony patches removed.");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            _patched = false;
        }
    }
}
