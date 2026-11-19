using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// Rain streaks near the camera: one mesh of random seeds that RainDrops.shader places in
    /// world space, wraps around the camera and masks with <see cref="CloudRain"/>. See the
    /// shader for why this does not read as a filter on the lens the way the game's does.
    /// </summary>
    /// <remarks>
    /// Rain only. On a winter map the flakes near the camera are the GAME's (GameRain), shown
    /// only where it snows: ours -- round flakes, "_Snow" in the shader, a dormant path since --
    /// were built for 1.3.0 and replaced before it shipped ("I like the vanilla game's snow
    /// better").
    /// </remarks>
    public class RainDrops : MonoBehaviour
    {
        /// <summary>Four vertices a drop, and Unity 5.6 meshes stop at 65k vertices.</summary>
        private const int DropCount = 14000;
        private const float FarShare = 0.4f;
        private const float LogInterval = 5f;

        private static readonly Vector3 BoxNear = new Vector3(40f, 30f, 40f);
        private static readonly Vector3 BoxFar = new Vector3(170f, 110f, 170f);

        private static readonly int IdBoxNear = Shader.PropertyToID("_BoxNear");
        private static readonly int IdBoxFar = Shader.PropertyToID("_BoxFar");
        private static readonly int IdFallOffset = Shader.PropertyToID("_FallOffset");
        private static readonly int IdFallDirection = Shader.PropertyToID("_FallDirection");
        private static readonly int IdDropColor = Shader.PropertyToID("_DropColor");
        private static readonly int IdDropFade = Shader.PropertyToID("_DropFade");
        private static readonly int IdDropDensity = Shader.PropertyToID("_DropDensity");
        private static readonly int IdPixelAngle = Shader.PropertyToID("_PixelAngle");
        private static readonly int IdStreakNear = Shader.PropertyToID("_StreakNear");
        private static readonly int IdStreakFar = Shader.PropertyToID("_StreakFar");

        private static readonly int IdRainWind = Shader.PropertyToID("_RainWind");
        private static readonly int IdRainWindOffset = Shader.PropertyToID("_RainWindOffset");
        private static readonly int IdRainGust = Shader.PropertyToID("_RainGust");
        private static readonly int IdRainDetail = Shader.PropertyToID("_RainDetail");
        private static readonly int IdRainLife = Shader.PropertyToID("_RainLife");
        private static readonly int IdRainLife2 = Shader.PropertyToID("_RainLife2");
        private static readonly int IdRainLayer = Shader.PropertyToID("_RainLayer");

        /// <summary>
        /// The air's carry is wrapped to this: a whole number of every box (12, 40, 85 and 170 m) and of
        /// the eddies' pattern (17 m x 120, RainDrops.shader).
        /// </summary>
        private const float WindWrap = 2040f;

        private Vector2 _windCarried;
        private string _loggedWind;

        // The rain's life (2026-11-19, with Finer rain): the sheets' drift and the eddies' clock, on
        // simulation time; and a second draw of the same mesh: its near drops in the near box again (the
        // sheets take a third of the drops on average, and a dense sheet should be denser than the rain
        // was), its far drops in a box between the two for the middle distance.
        private static readonly Vector3 BoxMid = new Vector3(85f, 60f, 85f);
        private static readonly Vector4 SecondSeed = new Vector4(0.371f, 0.618f, 0.137f, 1f);
        private Vector2 _sheetDrift;
        private float _eddyClock;
        private GameObject _secondHolder;
        private MeshRenderer _secondRenderer;
        private MaterialPropertyBlock _secondBlock;
        private bool _loggedLife;

        /// <summary>The wind the streaks last read (m/s), 0 without Wind on the rain: the life's strength follows it.</summary>
        private float _windSpeed;
        private float _windGusts;
        private float _carrySpeed;
        private Vector3 _carryDirection = Vector3.forward;

        // Finer rain (2026-11-15): splashes (RainSplash.shader) and the city's lamps on the streaks.
        private const int SplashCount = 3000;
        private const float SplashBox = 30f;
        private const float SplashLives = 4f;        // lives a second: a splash lasts a quarter of a second
        /// <summary>
        /// How much of the lamp map's light a drop throws back (the map is the fog's, per its own unit).
        /// A field, not a constant: tuned from a probe. Since 2026-11-19 it is ADDED to the picture
        /// whatever the streaks' opacity (it was multiplied by it: at 15% nothing showed at a lamp).
        /// </summary>
        public static float LampOnRain = 0.6f;

        /// <summary>The splashes keep the share they were built with (their shader still blends by opacity).</summary>
        private const float LampOnSplash = 0.25f;

        /// <summary>How much of a lightning flash's colour (CloudLightning's, 5+ at full power) a drop throws back.</summary>
        public static float FlashOnRain = 0.04f;
        private static readonly int IdSplashParams = Shader.PropertyToID("_SplashParams");
        private GameObject _splashHolder;
        private MeshRenderer _splashRenderer;
        private Material _splashMaterial;
        private Mesh _splashMesh;
        private double _splashClock;
        private readonly RainCover _cover = new RainCover();
        private bool _loggedDetail;

        /// <summary>
        /// The rain wants the city's lamp map (FogLampMap keeps it while this is true, the fog off or not):
        /// Finer rain is on, it rains, the streaks are drawn.
        /// </summary>
        public static bool WantsLamps { get; private set; }

        private static bool Detail
        {
            get { return Settings.RainDetail != null && Settings.RainDetail.value; }
        }

        private static bool _shadersChecked;
        private static bool _shadersAvailable;

        /// <summary>
        /// Our rain only replaces the game's when BOTH shaders it needs are in the bundle: the
        /// streaks, and the invisible material that silences the game's rain shell.
        /// </summary>
        public static bool ShadersAvailable
        {
            get
            {
                if (!_shadersChecked)
                {
                    _shadersChecked = true;
                    _shadersAvailable = ShaderBundle.Get(ShaderBundle.RainDrops) != null
                                     && ShaderBundle.Get(ShaderBundle.Invisible) != null;
                }

                return _shadersAvailable;
            }
        }

        private CloudDensityField _field;
        private Camera _camera;
        private GameObject _holder;
        private MeshRenderer _renderer;
        private Material _material;
        private Mesh _mesh;
        private float _nextLogTime;
        private float _fade;
        private float _heightAboveGround;

        public void Initialise(CloudDensityField field)
        {
            _field = field;
        }

        private void Start()
        {
            if (!ShadersAvailable)
            {
                Log.Warn("rain: shaders missing from the bundle; the game's own rain stays.");
                enabled = false;
                return;
            }

            _material = new Material(ShaderBundle.Get(ShaderBundle.RainDrops)) { name = "VolumetricCloudsRain" };
            _mesh = BuildMesh();

            _holder = new GameObject("VolumetricCloudsRain");
            _holder.AddComponent<MeshFilter>().sharedMesh = _mesh;

            _renderer = _holder.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _renderer.enabled = false;

            Log.Msg("rain: " + DropCount + " streaks ready (" + _mesh.vertexCount + " vertices)");
        }

        /// <summary>
        /// The second draw (Finer rain): the same mesh and material, its seeds shifted and its boxes wider
        /// through a property block, so the rain has a middle distance. Made the first time it is wanted.
        /// </summary>
        private void UpdateSecond(bool want, float length, float width)
        {
            if (!want)
            {
                if (_secondRenderer != null)
                    _secondRenderer.enabled = false;
                return;
            }

            if (_secondHolder == null)
            {
                _secondHolder = new GameObject("VolumetricCloudsRainMiddle");
                _secondHolder.AddComponent<MeshFilter>().sharedMesh = _mesh;
                _secondRenderer = _secondHolder.AddComponent<MeshRenderer>();
                _secondRenderer.sharedMaterial = _material;
                _secondRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _secondRenderer.receiveShadows = false;
                _secondRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                _secondBlock = new MaterialPropertyBlock();
            }

            _secondHolder.transform.position = _camera.transform.position;
            _secondBlock.SetVector(IdBoxNear, BoxNear);
            _secondBlock.SetVector(IdBoxFar, BoxMid);
            _secondBlock.SetVector(IdStreakNear, new Vector4(1.1f * length, 0.012f * width, 0f, 0f));
            _secondBlock.SetVector(IdStreakFar, new Vector4(1.9f * length, 0.02f * width, 0f, 0f));
            _secondBlock.SetVector(IdRainLayer, SecondSeed);
            _secondRenderer.SetPropertyBlock(_secondBlock);
            _secondRenderer.enabled = true;
        }

        /// <summary>The splashes' mesh and material, made the first time Finer rain draws (false: no shader here).</summary>
        private bool EnsureSplashes()
        {
            if (_splashMaterial != null)
                return true;
            if (_splashHolder != null)
                return false;   // tried before, no shader

            _splashHolder = new GameObject("VolumetricCloudsRainSplashes");
            Shader shader = ShaderBundle.Get(ShaderBundle.RainSplash);
            if (shader == null)
            {
                Log.Msg("rain: splashes NOT AVAILABLE here (their shader is not in this platform's bundle)");
                return false;
            }

            _splashMaterial = new Material(shader) { name = "VolumetricCloudsRainSplash" };
            _splashMesh = BuildSplashMesh();
            _splashHolder.AddComponent<MeshFilter>().sharedMesh = _splashMesh;
            _splashRenderer = _splashHolder.AddComponent<MeshRenderer>();
            _splashRenderer.sharedMaterial = _splashMaterial;
            _splashRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _splashRenderer.receiveShadows = false;
            _splashRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _splashRenderer.enabled = false;
            Log.Msg("rain: " + SplashCount + " splash slots ready (a " + SplashBox.ToString("F0") + " m box round the camera, " +
                    SplashLives.ToString("F0") + " lives a second each)");
            return true;
        }

        private static Mesh BuildSplashMesh()
        {
            System.Random random = new System.Random(20261115);
            Vector3[] vertices = new Vector3[SplashCount * 4];
            Vector2[] corners = new Vector2[SplashCount * 4];
            List<Vector3> randoms = new List<Vector3>(SplashCount * 4);
            int[] triangles = new int[SplashCount * 6];
            for (int i = 0; i < SplashCount; i++)
            {
                Vector3 seed = new Vector3((float)random.NextDouble(), 0f, (float)random.NextDouble());
                Vector3 perSlot = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                int v = i * 4;
                for (int c = 0; c < 4; c++)
                {
                    vertices[v + c] = seed;
                    randoms.Add(perSlot);
                }
                corners[v] = new Vector2(-1f, 0f);
                corners[v + 1] = new Vector2(1f, 0f);
                corners[v + 2] = new Vector2(1f, 1f);
                corners[v + 3] = new Vector2(-1f, 1f);
                int t = i * 6;
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v; triangles[t + 4] = v + 2; triangles[t + 5] = v + 3;
            }
            Mesh mesh = new Mesh { name = "VolumetricCloudsRainSplashes" };
            mesh.vertices = vertices;
            mesh.uv = corners;
            mesh.SetUVs(1, randoms);
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            return mesh;
        }

        /// <summary>Finer rain's splashes: drawn with the streaks while it is on, on simulation time.</summary>
        private void UpdateSplashes(bool draw, float streaks)
        {
            bool want = draw && Detail && EnsureSplashes();
            if (_splashRenderer != null)
                _splashRenderer.enabled = want;
            if (!want)
                return;

            SimulationManager simulation = Singleton<SimulationManager>.exists ? Singleton<SimulationManager>.instance : null;
            bool paused = simulation == null || simulation.SimulationPaused || simulation.ForcedSimulationPaused;
            if (!paused)
                _splashClock = (_splashClock + Time.deltaTime * Mathf.Min(2, Mathf.Max(1, simulation.FinalSimulationSpeed))) % 3600.0;

            Vector3 eye = _camera.transform.position;
            float ground = Singleton<TerrainManager>.exists ? Singleton<TerrainManager>.instance.SampleRawHeightSmooth(eye) : 0f;
            _splashHolder.transform.position = eye;
            CloudShaderParams.Apply(_splashMaterial, _field, null);
            _splashMaterial.SetVector(IdSplashParams, new Vector4((float)_splashClock, ground, SplashBox, SplashLives));
            _splashMaterial.SetVector(IdDropColor, _material.GetVector(IdDropColor));
            _splashMaterial.SetFloat(IdDropDensity, streaks);
            _splashMaterial.SetFloat(IdDropFade, _fade);
            _splashMaterial.SetVector(IdRainDetail, new Vector4(1f, LampOnSplash, 0f, 0f));
        }

        private void LateUpdate()
        {
            if (_material == null || _field == null)
                return;

            if (_camera == null)
                _camera = GameCamera.Get();
            if (_camera == null)
                return;

            float streaks = Settings.RainStreaks != null ? Mathf.Max(0f, Settings.RainStreaks.value) : 1f;
            _fade = HeightFade();

            // Nothing to draw: no rain, switched off, the camera too high for streaks -- or snow,
            // whose flakes are the game's own (GameRain).
            bool draw = CloudRain.Active && !CloudRain.IsSnow && CloudRain.Amount > 0.001f && streaks > 0f && _fade > 0.001f;
            _renderer.enabled = draw;

            if (draw)
            {
                // The shader places every drop itself; the object only has to stay in view.
                _holder.transform.position = _camera.transform.position;
                UpdateMaterial(streaks);
            }
            else
            {
                UpdateSecond(false, 1f, 1f);
            }

            // Finer rain: the lamp map for the streaks and splashes (FogLampMap reads this), the splashes,
            // and the cover that keeps the rain from under roofs and bridges (nothing of it exists otherwise).
            WantsLamps = draw && Detail;
            UpdateSplashes(draw, streaks);
            if (draw && Detail)
            {
                _cover.Apply(_camera.transform.position, _material, _splashMaterial);
            }
            else
            {
                _cover.Release();
                RainCover.Off(_material, _splashMaterial);
            }
            if (Detail && draw && !_loggedDetail)
            {
                _loggedDetail = true;
                Log.Msg("rain: finer rain on -- drop sizes, drops at the lens, a second layer of streaks for the middle distance, " +
                        "sheets and eddies, splashes, and the city's lamps and the lightning on the rain");
            }
            else if (!Detail)
            {
                _loggedDetail = false;
            }

            if (Log.Detailed && Time.time >= _nextLogTime)
            {
                _nextLogTime = Time.time + LogInterval;
                Log.Detail("rain: active=" + CloudRain.Active + (CloudRain.IsSnow ? " (winter map: snow, the game's flakes)" : "") +
                        " amount=" + CloudRain.Amount.ToString("F2") +
                        " rainsUnder=" + (CloudRain.RainCoverage * 100f).ToString("F0") + "% of sky" +
                        " atCamera=" + CloudRain.LocalRain(_camera.transform.position).ToString("F2") +
                        " | streaks " + (draw ? "drawn" : "hidden") +
                        " cameraHeight=" + _heightAboveGround.ToString("F0") + "m fade=" + _fade.ToString("F2") +
                        " look: length=" + Percent(Settings.RainStreakLength) + " width=" + Percent(Settings.RainStreakWidth) +
                        " opacity=" + Percent(Settings.RainStreakOpacity) +
                        " colour=" + (Settings.RainStreakColor != null ? ColorText.Format(Settings.RainStreakColor.value) : "default") +
                        " fallSpeed=" + Percent(Settings.RainFallSpeed) +
                        " | slant=(" + CloudRain.Slant.x.ToString("F2") + "," + CloudRain.Slant.z.ToString("F2") + ")");
            }
        }

        /// <summary>
        /// Streaks belong to street level. From a city-wide view they are what made the game's
        /// rain a filter on the lens, so they fade out with height and the rain curtains in
        /// the cloud pass take over.
        /// </summary>
        private float HeightFade()
        {
            Vector3 position = _camera.transform.position;
            float ground = Singleton<TerrainManager>.exists
                ? Singleton<TerrainManager>.instance.SampleRawHeightSmooth(position)
                : 0f;

            _heightAboveGround = position.y - ground;

            float limit = Settings.RainStreakHeight != null ? Mathf.Max(50f, Settings.RainStreakHeight.value) : 450f;
            return 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(limit * 0.4f, limit, _heightAboveGround));
        }

        private void UpdateMaterial(float streaks)
        {
            // Where it rains, the wind, the cloud base: everything SampleRain needs.
            CloudShaderParams.Apply(_material, _field, null);

            _material.SetVector(IdBoxNear, BoxNear);
            _material.SetVector(IdBoxFar, BoxFar);
            _material.SetVector(IdFallOffset, CloudRain.FallOffset);
            _material.SetVector(IdFallDirection, CloudRain.FallDirection);
            float length = Multiplier(Settings.RainStreakLength, Settings.Defaults.RainStreakLength, 0.1f);
            float width = Multiplier(Settings.RainStreakWidth, Settings.Defaults.RainStreakWidth, 0.1f);

            _material.SetVector(IdStreakNear, new Vector4(1.1f * length, 0.012f * width, 0f, 0f));
            _material.SetVector(IdStreakFar, new Vector4(3.2f * length, 0.035f * width, 0f, 0f));
            _material.SetFloat(IdDropFade, _fade);
            _material.SetFloat(IdDropDensity, streaks);

            // One pixel's angle, so the shader can keep a streak from going sub-pixel.
            float pixelAngle = 2f * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, _camera.pixelHeight);
            _material.SetFloat(IdPixelAngle, pixelAngle);

            _material.SetVector(IdDropColor, StreakLook(DropColor()));

            // Simulation seconds this frame: paused, the wind's carry, the sheets and the eddies hold.
            SimulationManager simulation = Singleton<SimulationManager>.exists ? Singleton<SimulationManager>.instance : null;
            float rate = simulation == null || simulation.SimulationPaused || simulation.ForcedSimulationPaused
                ? 0f : Mathf.Min(Mathf.Max(1, simulation.FinalSimulationSpeed), 2f);
            float seconds = Time.deltaTime * rate;

            UpdateWind(seconds);

            // x: drop sizes and the lens drops; y, z: the city's lamps (the map is drawn only at night) and
            // the lightning on the rain; w: both by the Streak brightness row.
            bool detail = Detail;
            float opacity = Multiplier(Settings.RainStreakOpacity, Settings.Defaults.RainStreakOpacity, 0f);
            _material.SetVector(IdRainDetail, detail
                ? new Vector4(1f, LampOnRain, FlashOnRain, RainLook.LightShare(opacity))
                : Vector4.zero);
            _material.SetVector(IdRainLayer, Vector4.zero);
            if (detail)
                CloudLightning.ApplyTo(_material);

            UpdateLife(detail, seconds);
            UpdateSecond(detail, length, width);
        }

        /// <summary>
        /// Wind on the rain: the row blends the streaks' lean from the curtains' own (0%, as they always
        /// fell) to what the city's wind gives (100%; RainLook.NaturalLean: gentle in the weather's own
        /// wind, steep only in a gale, with a ceiling), and the gusts change and swing it a little as the
        /// fronts roll through. Until 2026-11-19 the lean was wind / 7.5 m/s with no ceiling, x 2.5 in a
        /// gust, and a gust shoved the drops sideways: "like the rain is becoming horizontal".
        /// </summary>
        private void UpdateWind(float seconds)
        {
            float row = Settings.RainWind != null ? Mathf.Clamp(Settings.RainWind.value, 0f, 2f) : 0f;
            bool on = row > 0f && WeatherWind.Advanced;

            // The curtains' lean and the fall speed, as CloudRain moves the streaks without the wind.
            Vector3 slant = CloudRain.Slant;
            float fall = 10f * Mathf.Max(0.1f, Settings.RainFallSpeed != null ? Settings.RainFallSpeed.value : 1f);

            if (!on)
            {
                _windSpeed = 0f;
                _windGusts = 0f;
                _carrySpeed = fall * new Vector2(slant.x, slant.z).magnitude;
                _carryDirection = CloudWeather.WindDirection;
                _material.SetVector(IdRainWind, Vector4.zero);
                if (_loggedWind != null)
                {
                    _loggedWind = null;
                    Log.Msg("rain: wind on the rain off -- the streaks fall along the curtains' slant again");
                }
                return;
            }

            // The mean lean, a vector: from the curtains' slant to the wind's own, by the row.
            float wind = WeatherWind.Speed;
            Vector3 dir = WeatherWind.Direction;
            float natural = RainLook.NaturalLean(RainLook.WindRead(row, wind));
            float share = RainLook.WindShare(row);
            Vector2 lean = Vector2.Lerp(new Vector2(slant.x, slant.z), new Vector2(dir.x, dir.z) * natural, share);

            // The streaks fall at the row's speed; the air carries them in the same proportion, so a
            // streak moves along its own length.
            _windCarried = new Vector2(Mathf.Repeat(_windCarried.x + lean.x * fall * seconds, WindWrap),
                                       Mathf.Repeat(_windCarried.y + lean.y * fall * seconds, WindWrap));

            TreeWind.Globals g = WeatherWind.Globals;
            float gusts = Mathf.Clamp01(WeatherWind.Gusts);
            float gustShare = RainLook.GustShare(row, gusts);
            float swing = RainLook.SwingShare(row, gusts);

            _material.SetVector(IdRainWind, new Vector4(lean.x, gustShare, lean.y, 1f));
            _material.SetVector(IdRainWindOffset, new Vector4(_windCarried.x, RainLook.Bands(row, gusts), _windCarried.y, swing));
            _material.SetVector(IdRainGust, new Vector4(g.GustX, g.GustZ, g.InvCell, gusts));

            _windSpeed = wind;
            _windGusts = gusts;
            _carrySpeed = fall * lean.magnitude;
            _carryDirection = lean.sqrMagnitude > 1e-6f ? new Vector3(lean.x, 0f, lean.y).normalized : dir;

            string kind = TreeWind.Describe(WeatherWind.Breezes);
            if (kind != _loggedWind)
            {
                _loggedWind = kind;
                float mean = lean.magnitude;
                Log.Msg("rain: the wind is on the rain -- " + wind.ToString("F1") + " m/s (" + kind + "), row " +
                        (row * 100f).ToString("F0") + "%: streaks lean " + RainLook.Degrees(mean).ToString("F0") +
                        " deg from upright (the curtains' " + RainLook.Degrees(new Vector2(slant.x, slant.z).magnitude).ToString("F0") +
                        ", the wind's own " + RainLook.Degrees(natural).ToString("F0") + "), " +
                        RainLook.Degrees(mean * (1f - gustShare)).ToString("F0") + " to " +
                        RainLook.Degrees(mean * (1f + gustShare)).ToString("F0") + " in lulls and gusts, swinging +-" +
                        RainLook.Degrees(swing).ToString("F0") + " deg");
            }
        }

        /// <summary>
        /// The rain's life (2026-11-19, part of Finer rain; the author: "make it more dynamic/random"):
        /// sheets of denser and thinner rain riding downwind, eddies that wind the drops' paths, and each
        /// drop's own speed, length and brightness (the shader's). Off = the streaks as before.
        /// </summary>
        private void UpdateLife(bool on, float seconds)
        {
            if (!on)
            {
                _material.SetVector(IdRainLife, Vector4.zero);
                _material.SetVector(IdRainLife2, Vector4.zero);
                _loggedLife = false;
                return;
            }

            // The sheets ride downwind with the rain the air carries, and never stand still.
            float speed = RainLook.SheetSpeed(_carrySpeed);
            _sheetDrift = new Vector2(Mathf.Repeat(_sheetDrift.x + _carryDirection.x * speed * seconds, RainLook.SheetWrap),
                                      Mathf.Repeat(_sheetDrift.y + _carryDirection.z * speed * seconds, RainLook.SheetWrap));
            _eddyClock = Mathf.Repeat(_eddyClock + seconds / RainLook.EddyTurn, 1f);

            float depth = RainLook.SheetDepth(_windSpeed, _windGusts);
            float reach = RainLook.EddyReach(_windSpeed);
            // The sheets lean with the fall: half as much as the streaks.
            Vector3 slope = _carryDirection * (0.5f * _carrySpeed / Mathf.Max(1f, 10f * Mathf.Max(0.1f, Settings.RainFallSpeed != null ? Settings.RainFallSpeed.value : 1f)));

            _material.SetVector(IdRainLife, new Vector4(_sheetDrift.x, _sheetDrift.y, _eddyClock, reach));
            _material.SetVector(IdRainLife2, new Vector4(depth, 0f, slope.x, slope.z));

            if (!_loggedLife)
            {
                _loggedLife = true;
                Log.Msg("rain: sheets and eddies -- sheets take up to " + (depth * 100f).ToString("F0") + "% of the drops in a thin stretch, riding at " +
                        speed.ToString("F0") + " m/s; eddies wind the drops " + reach.ToString("F2") + " m to each side (" +
                        RainLook.Degrees(reach * 2f * Mathf.PI / 24f).ToString("F0") + " deg off the lean at most)");
            }
        }

        private static string Percent(FloatSetting setting)
        {
            return setting != null ? (setting.value * 100f).ToString("F0") + "%" : "default";
        }

        private static float Multiplier(FloatSetting setting, float fallback, float min)
        {
            return Mathf.Max(min, setting != null ? setting.value : fallback);
        }

        /// <summary>The picked hue (white = exactly 1, CloudTint) and the opacity multiplier.</summary>
        private static Vector4 StreakLook(Vector4 color)
        {
            Color tint = CloudTint.Of(Settings.RainStreakColor != null
                ? Settings.RainStreakColor.value : Settings.Defaults.RainStreakColor);
            float opacity = Multiplier(Settings.RainStreakOpacity, Settings.Defaults.RainStreakOpacity, 0f);

            // Alpha capped at 1: the shader does not clamp, and above 1 the blend darkens.
            return new Vector4(color.x * tint.r, color.y * tint.g, color.z * tint.b,
                               Mathf.Min(1f, color.w * opacity));
        }

        /// <summary>
        /// Streaks catch the sky: the scene's ambient, lifted, with a little of the key light. The
        /// city's ambient (SceneAmbient.Air), not the clouds': they fall through the city's air.
        /// </summary>
        private static Vector4 DropColor()
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;

            Color ambient = SceneAmbient.Air();
            if (linear)
                ambient = ambient.linear;

            Color color = ambient * 1.4f;

            DayNightProperties properties = DayNightProperties.instance;
            Light sun = properties != null && properties.m_SunLight != null
                ? properties.m_SunLight.GetComponent<Light>() : null;
            if (sun != null)
                color += (linear ? sun.color.linear : sun.color) * (sun.intensity * 0.04f);

            // Visible against a night sky, never glowing against a day one.
            return new Vector4(Mathf.Clamp(color.r, 0.05f, 1.2f),
                               Mathf.Clamp(color.g, 0.05f, 1.2f),
                               Mathf.Clamp(color.b, 0.06f, 1.2f),
                               0.5f);
        }

        /// <summary>
        /// A cloud of seeds, not of positions: POSITION is random in 0..1 and the shader turns
        /// it into a place in the world. See RainDrops.shader for the layout.
        /// </summary>
        private static Mesh BuildMesh()
        {
            System.Random random = new System.Random(20260920);

            Vector3[] vertices = new Vector3[DropCount * 4];
            Vector2[] corners = new Vector2[DropCount * 4];
            List<Vector3> randoms = new List<Vector3>(DropCount * 4);
            int[] triangles = new int[DropCount * 6];

            for (int i = 0; i < DropCount; i++)
            {
                Vector3 seed = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                Vector3 perDrop = new Vector3(
                    (float)random.NextDouble(),                          // rank: shown once the rain is this strong
                    random.NextDouble() < FarShare ? 1f : 0f,            // layer
                    (float)random.NextDouble());                         // speed variation

                int v = i * 4;
                for (int c = 0; c < 4; c++)
                {
                    vertices[v + c] = seed;
                    randoms.Add(perDrop);
                }

                corners[v + 0] = new Vector2(-1f, 0f);
                corners[v + 1] = new Vector2(1f, 0f);
                corners[v + 2] = new Vector2(1f, 1f);
                corners[v + 3] = new Vector2(-1f, 1f);

                int t = i * 6;
                triangles[t + 0] = v + 0;
                triangles[t + 1] = v + 1;
                triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 0;
                triangles[t + 4] = v + 2;
                triangles[t + 5] = v + 3;
            }

            Mesh mesh = new Mesh { name = "VolumetricCloudsRainDrops" };
            mesh.vertices = vertices;
            mesh.uv = corners;
            mesh.SetUVs(1, randoms);
            mesh.triangles = triangles;

            // The real positions only exist in the shader, so the bounds say "always visible".
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            return mesh;
        }

        private void OnDestroy()
        {
            WantsLamps = false;
            _cover.Release();
            if (_splashHolder != null)
                Destroy(_splashHolder);
            if (_splashMesh != null)
                Destroy(_splashMesh);
            if (_splashMaterial != null)
                Destroy(_splashMaterial);
            if (_secondHolder != null)
                Destroy(_secondHolder);
            if (_holder != null)
                Destroy(_holder);
            if (_mesh != null)
                Destroy(_mesh);
            if (_material != null)
                Destroy(_material);
        }
    }
}
