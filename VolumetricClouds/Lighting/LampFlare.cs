using UnityEngine;

namespace VolumetricClouds.Lighting
{
    /// <summary>
    /// THE FLARE'S SETTINGS, for this mod's halos AND for other mods' lights (2026-11-17, the author:
    /// every lighting mod's flare is controlled from here, "because it has the most flare controls").
    /// Other mods read these by reflection (assembly "VolumetricClouds"), every frame; nothing here
    /// references them.
    /// </summary>
    /// <remarks>
    /// AN API, like <c>Sky.WeatherWind</c>: these names, types and units never change. A new meaning is a new
    /// member and <see cref="ApiVersion"/> 2 (readers check &gt;= 1). Each value is the player's setting NOW,
    /// clamped as the halos use it, whether or not the replacement halos are on. Without the settings
    /// (before they are read) each is its default: 1, 1, 1, 1, 1, 1, 6, 280.
    /// Near lamps are those closer than <see cref="NearDistance"/>, distant ones past twice it; between,
    /// the near and far values blend (smoothstep), as the halos do.
    /// </remarks>
    public static class LampFlare
    {
        public const int ApiVersion = 1;

        /// <summary>0..3: the flare's strength on near lights; 1 = as built, 0 = none (HaloFlareNear).</summary>
        public static float NearStrength
        {
            get { return Mathf.Clamp(Value(Settings.HaloFlareNear, Settings.Defaults.HaloFlareNear), 0f, 3f); }
        }

        /// <summary>0..3: on distant lights (HaloFlareFar).</summary>
        public static float FarStrength
        {
            get { return Mathf.Clamp(Value(Settings.HaloFlareFar, Settings.Defaults.HaloFlareFar), 0f, 3f); }
        }

        /// <summary>0.25..4: multiplies the rays' length on near lights (HaloFlareSizeNear).</summary>
        public static float NearSize
        {
            get { return Mathf.Clamp(Value(Settings.HaloFlareSizeNear, Settings.Defaults.HaloFlareSizeNear), 0.25f, 4f); }
        }

        /// <summary>0.25..4: on distant lights (HaloFlareSizeFar).</summary>
        public static float FarSize
        {
            get { return Mathf.Clamp(Value(Settings.HaloFlareSizeFar, Settings.Defaults.HaloFlareSizeFar), 0.25f, 4f); }
        }

        /// <summary>0.5..3: multiplies a ray's width (HaloFlareThickness).</summary>
        public static float Thickness
        {
            get { return Mathf.Clamp(Value(Settings.HaloFlareThickness, Settings.Defaults.HaloFlareThickness), 0.5f, 3f); }
        }

        /// <summary>0..2: 0 only the fine rays, 1 both as built, 2 only the long spikes (HaloFlareSpikes).</summary>
        public static float Spikes
        {
            get { return Mathf.Clamp(Value(Settings.HaloFlareSpikes, Settings.Defaults.HaloFlareSpikes), 0f, 2f); }
        }

        /// <summary>4, 6 or 8 long spikes (HaloFlareSpikeCount); any other number in the file: 6.</summary>
        public static int SpikeCount
        {
            get
            {
                int n = Settings.HaloFlareSpikeCount != null ? Settings.HaloFlareSpikeCount.value : Settings.Defaults.HaloFlareSpikeCount;
                return n == 4 || n == 6 || n == 8 ? n : Settings.Defaults.HaloFlareSpikeCount;
            }
        }

        /// <summary>Metres: near values inside, far past twice it, blended between (HaloNearLightDistance); 0 = every light is a distant one.</summary>
        public static float NearDistance
        {
            get { return Mathf.Max(0f, Value(Settings.HaloNearLightDistance, Settings.Defaults.HaloNearDistance)); }
        }

        /// <summary>0..3: vehicles' lights in this mod's halos (HaloFlareVehicles). Not read by other mods.</summary>
        public static float VehicleStrength
        {
            get { return Mathf.Clamp(Value(Settings.HaloFlareVehicles, Settings.Defaults.HaloFlareVehicles), 0f, 3f); }
        }

        /// <summary>0.25..4: the vehicles' rays' length (HaloFlareSizeVehicles). Not read by other mods.</summary>
        public static float VehicleSize
        {
            get { return Mathf.Clamp(Value(Settings.HaloFlareSizeVehicles, Settings.Defaults.HaloFlareSizeVehicles), 0.25f, 4f); }
        }

        private static float Value(FloatSetting setting, float fallback)
        {
            return setting != null ? setting.value : fallback;
        }
    }
}
