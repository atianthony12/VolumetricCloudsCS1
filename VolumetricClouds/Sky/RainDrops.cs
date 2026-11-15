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

        /// <summary>
        /// A drop of medium size falls this fast in still air (m/s): what the wind's lean is reckoned
        /// against, whatever the Rain fall speed row makes the streaks do (it scales how fast they move,
        /// not which way).
        /// </summary>
        public const float StillAirFall = 7.5f;

        /// <summary>The air's carry is wrapped to this: a whole number of every box (12, 40 and 170 m).</summary>
        private const float WindWrap = 2040f;

        private Vector2 _windCarried;
        private string _loggedWind;

        // Finer rain (2026-11-15): splashes (RainSplash.shader) and the city's lamps on the streaks.
        private const int SplashCount = 3000;
        private const float SplashBox = 30f;
        private const float SplashLives = 4f;        // lives a second: a splash lasts a quarter of a second
        /// <summary>How much of the lamp map's light a drop catches (the map is the fog's, per its own unit).</summary>
        private const float LampOnRain = 0.25f;
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
            _splashMaterial.SetVector(IdRainDetail, _material.GetVector(IdRainDetail));
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
                Log.Msg("rain: finer rain on -- drop sizes, drops at the lens, splashes, and the city's lamps on the rain at night");
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

            UpdateWind();
            // x: drop sizes and the lens drops; y: the city's lamps on the rain (the map is drawn only at night).
            bool detail = Detail;
            _material.SetVector(IdRainDetail, new Vector4(detail ? 1f : 0f, detail ? LampOnRain : 0f, 0f, 0f));
        }

        /// <summary>
        /// Wind on the rain (2026-11-15): the city's wind (WeatherWind) leans the streaks by its speed
        /// over a drop's fall speed in still air and carries them, its gusts pushing and leaning them
        /// more as the fronts roll through. 0 = the streaks as before.
        /// </summary>
        private void UpdateWind()
        {
            float row = Settings.RainWind != null ? Mathf.Clamp(Settings.RainWind.value, 0f, 2f) : 0f;
            bool on = row > 0f && WeatherWind.Advanced;
            if (!on)
            {
                _material.SetVector(IdRainWind, Vector4.zero);
                if (_loggedWind != null)
                {
                    _loggedWind = null;
                    Log.Msg("rain: wind on the rain off -- the streaks fall along the curtains' slant again");
                }
                return;
            }

            float speed = WeatherWind.Speed * row;
            Vector3 dir = WeatherWind.Direction;
            float lean = speed / StillAirFall;

            // The streaks fall at the row's speed; the air carries them in the same proportion, so a
            // streak moves along its own length.
            float fall = 10f * Mathf.Max(0.1f, Settings.RainFallSpeed != null ? Settings.RainFallSpeed.value : 1f);
            SimulationManager simulation = Singleton<SimulationManager>.exists ? Singleton<SimulationManager>.instance : null;
            float rate = simulation == null || simulation.SimulationPaused || simulation.ForcedSimulationPaused
                ? 0f : Mathf.Min(Mathf.Max(1, simulation.FinalSimulationSpeed), 2f);
            float carry = fall * lean * Time.deltaTime * rate;
            _windCarried = new Vector2(Mathf.Repeat(_windCarried.x + dir.x * carry, WindWrap),
                                       Mathf.Repeat(_windCarried.y + dir.z * carry, WindWrap));

            TreeWind.Globals g = WeatherWind.Globals;
            float gusts = Mathf.Clamp01(WeatherWind.Gusts);
            // A gust pushes a drop about 1.5 s of the wind's carry, at most half of it each way.
            float push = gusts * Mathf.Min(fall * lean, 40f) * 1.5f;

            _material.SetVector(IdRainWind, new Vector4(dir.x * lean, push, dir.z * lean, 1f));
            _material.SetVector(IdRainWindOffset, new Vector4(_windCarried.x, gusts, _windCarried.y, 0f));
            _material.SetVector(IdRainGust, new Vector4(g.GustX, g.GustZ, g.InvCell, gusts));

            string kind = TreeWind.Describe(WeatherWind.Breezes);
            if (kind != _loggedWind)
            {
                _loggedWind = kind;
                Log.Msg("rain: the wind is on the rain -- " + speed.ToString("F1") + " m/s (" + kind + ", row " +
                        (row * 100f).ToString("F0") + "%), streaks lean " + (Mathf.Atan(lean) * Mathf.Rad2Deg).ToString("F0") +
                        " deg from upright, gusts +-" + (gusts * 100f).ToString("F0") + "%");
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
            if (_holder != null)
                Destroy(_holder);
            if (_mesh != null)
                Destroy(_mesh);
            if (_material != null)
                Destroy(_material);
        }
    }
}
