using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// THE wind of the city (2026-10-30; the author: "the central place to control wind is Volumetric
    /// Weather"): how strong it blows, from the weather or the Now tab's override, which way (the Now
    /// tab's wind direction, eased), and the gusts rolling downwind across the map. The trees, the
    /// leaves and dust they lose (TreeShaders, WindDebris) and other mods' wind -- Real Flag's flags --
    /// all follow this one. Advanced once a frame by CloudLighting, but ONLY while something needs it
    /// (2026-11-02, the author: "never let our mod add anything to the user's game, even behind the
    /// scenes, unless it's needed / toggled on"): the trees' wind is on, or another mod has read
    /// <see cref="Ready"/> in this city. Otherwise nothing here runs and nothing is logged.
    /// </summary>
    /// <remarks>
    /// FOR OTHER MODS (read by reflection; these names and meanings stay, <see cref="ApiVersion"/> says
    /// what is here; main thread):
    /// - <see cref="Ready"/>: true once advanced in this city; until then read your own wind. Reading it
    ///   is what asks for the wind: from the next frame on it is advanced for the rest of the city.
    /// - <see cref="Direction"/>: where the wind blows TO, level, unit -- the game's convention
    ///   (sin a, 0, cos a), the way the clouds, fog and rain move.
    /// - <see cref="Speed"/>: the mean wind at 10 m over open ground, m/s.
    /// - <see cref="Gusts"/>: how far a gust lifts it (and a lull lowers it), 0..0.9 of the mean.
    /// - <see cref="SpeedAt"/>(x, z): the wind at 10 m at a place, m/s, with the gust passing there now.
    /// - <see cref="GustAt"/>(x, z): that gust, 0 a lull .. 1 a gust.
    /// - <see cref="Overridden"/>: the player set it by hand on the Now tab.
    /// </remarks>
    public static class WeatherWind
    {
        public const int ApiVersion = 1;

        /// <summary>
        /// m/s at 10 m a breeze: Beaufort's names then match TreeWind.Describe's -- "breezy" (1) 6 m/s,
        /// a moderate breeze; "stormy" (2) 12 m/s, a strong breeze; "gale" (3) 18 m/s, a gale.
        /// </summary>
        public const float MetresPerBreeze = 6f;

        private static TreeWind.Wind _wind;
        private static TreeWind.Motion _motion;
        private static TreeWind.Globals _globals;
        private static float _angle;
        private static float _storm;
        private static bool _started;
        private static bool _advanced;
        private static bool _asked;

        /// <summary>
        /// True once advanced in this city. Read by other mods only (our own code reads _advanced): a
        /// read is the request for the wind (<see cref="Asked"/>).
        /// </summary>
        public static bool Ready
        {
            get
            {
                _asked = true;
                return _advanced;
            }
        }

        /// <summary>Another mod has read <see cref="Ready"/> in this city (the Now tab's wind rows then count).</summary>
        internal static bool Asked
        {
            get { return _asked; }
        }

        /// <summary>Something needs the city's wind: the trees' wind is on, or another mod reads it.</summary>
        internal static bool Wanted
        {
            get { return _asked || (Settings.TreeWind != null && Settings.TreeWind.value); }
        }

        public static bool Overridden { get; private set; }

        /// <summary>Where the wind blows to (level, unit).</summary>
        public static Vector3 Direction
        {
            get
            {
                float a = _angle * Mathf.Deg2Rad;
                return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            }
        }

        /// <summary>The same, degrees in the game's convention (WeatherManager.m_windDirection's).</summary>
        public static float Angle
        {
            get { return _angle; }
        }

        /// <summary>The mean wind in breezes (TreeWind's unit), eased.</summary>
        public static float Breezes
        {
            get { return _wind.Speed; }
        }

        /// <summary>The mean wind at 10 m over open ground, m/s.</summary>
        public static float Speed
        {
            get { return _wind.Speed * MetresPerBreeze; }
        }

        public static float Gusts
        {
            get { return _wind.Gusts; }
        }

        /// <summary>A waterspout about, eased over 20 s (0..1): part of the wind.</summary>
        public static float Storm
        {
            get { return _storm; }
        }

        /// <summary>The gusts' clock and drift, which the trees' shaders and the leaves run on.</summary>
        public static TreeWind.Motion Motion
        {
            get { return _motion; }
        }

        /// <summary>The gust at a place now: 0 a lull .. 1 a gust (the trees' VWGust, the same field).</summary>
        public static float GustAt(float x, float z)
        {
            return _advanced ? TreeWind.GustAt(_globals, x, z) : 0.5f;
        }

        /// <summary>The wind at 10 m at a place now, m/s, the passing gust in it.</summary>
        public static float SpeedAt(float x, float z)
        {
            return Speed * Mathf.Max(0f, 1f + _wind.Gusts * (2f * GustAt(x, z) - 1f));
        }

        /// <summary>
        /// Once a frame from CloudLighting, after the wind's direction, the cover, the rain and the
        /// funnels. Simulation time: paused, the wind holds and the gusts stand still.
        /// </summary>
        public static void Advance(float deltaTime, float simulationRate)
        {
            // Off means nothing runs (invariant 10): nothing needs it, so it stops (and starts again
            // from the weather of the moment when something does).
            if (!Wanted)
            {
                if (_started)
                    Stop();
                return;
            }

            float storm = Funnels.Count > 0 ? 1f : 0f;
            _storm = _started ? Mathf.MoveTowards(_storm, storm, deltaTime / 20f) : storm;
            Overridden = Settings.WindOverride != null && Settings.WindOverride.value;
            TreeWind.Wind target = TreeWind.Compute(1f, Value(Settings.TreeGusts, Settings.Defaults.TreeGusts), 1f,
                                                    Value(Settings.TreeStorms, Settings.Defaults.TreeStorms),
                                                    CloudWeather.Coverage, CloudWeather.Rain, _storm,
                                                    Overridden ? Value(Settings.WindStrength, Settings.Defaults.WindStrength) : -1f);

            // The wind follows a change over a few seconds of simulation; when the game's swings round,
            // it comes round over several (TreeWind.Turn).
            float seconds = deltaTime * simulationRate;
            Vector3 d = CloudWeather.WindDirection;
            float weather = Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f);
            if (!_started)
            {
                _started = true;
                _wind = target;
                _angle = weather;
            }
            else
            {
                _wind = TreeWind.Ease(_wind, target, 1f - Mathf.Exp(-seconds / TreeWind.EaseSeconds));
                _angle = TreeWind.Turn(_angle, weather, seconds);
            }

            Vector3 dir = Direction;
            _motion.Advance(seconds, dir.x, dir.z, _wind.GustSpeed);
            _globals = TreeWind.GlobalsOf(_wind, _motion, dir.x, dir.z);
            _advanced = true;

            // One line when it starts and whenever the wind changes its name or its source.
            string kind = TreeWind.Describe(_wind.Speed) + (Overridden ? " (set)" : "");
            if (kind != _loggedKind)
            {
                _loggedKind = kind;
                Log.Msg("wind: the city's wind is " + Describe() + " -- followed by " +
                        (Settings.TreeWind != null && Settings.TreeWind.value ? "the trees" : "") +
                        (Settings.TreeWind != null && Settings.TreeWind.value && _asked ? " and " : "") +
                        (_asked ? "another mod reading it (Real Flag)" : ""));
            }
        }

        private static string _loggedKind;

        /// <summary>Nothing needs the wind any more: it stops, said once.</summary>
        private static void Stop()
        {
            _started = false;
            _advanced = false;
            _loggedKind = null;
            Overridden = false;
            Log.Msg("wind: the city's wind stopped (the trees' wind is off and no other mod reads it)");
        }

        /// <summary>A new city starts from its own weather, and with nothing asking for the wind.</summary>
        public static void Reset()
        {
            _started = false;
            _advanced = false;
            _asked = false;
            _loggedKind = null;
            Overridden = false;
        }

        /// <summary>For the log: "4.1 m/s (0.68 breezes, light breeze) toward 274 deg, gusts +-45%".</summary>
        public static string Describe()
        {
            return Speed.ToString("F1") + " m/s at 10 m (" + _wind.Speed.ToString("F2") + " breezes, " + TreeWind.Describe(_wind.Speed) +
                   (Overridden ? ", set on the Now tab" : ", from the weather") + ") toward " + _angle.ToString("F0") + " deg, gusts +-" +
                   (100f * _wind.Gusts).ToString("F0") + "%";
        }

        private static float Value(FloatSetting s, float fallback)
        {
            return s != null ? s.value : fallback;
        }
    }
}
