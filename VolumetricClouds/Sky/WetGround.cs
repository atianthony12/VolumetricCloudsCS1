using ColossalFramework;
using UnityEngine;
using UnityEngine.Rendering;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// "Wet roads and puddles" (2026-11-15): in and after rain the ground darkens, shines and mirrors
    /// the city, puddles stand on level hard ground with rings from the drops landing in them. One
    /// command buffer on the game's camera after its final pass (WetGround.shader has the look).
    /// </summary>
    /// <remarks>
    /// How wet: the game's own m_groundWetness (it rises in rain and dries after by the game's rule; read,
    /// never written: invariant 2), x the row. Nothing here touches a game material or _WeatherParams: the
    /// game's own slight wet look stays underneath.
    ///
    /// Off means nothing runs (invariant 10): with the row at 0, the ground dry, a winter map, the weather
    /// hidden or the camera high over the city, the buffer is taken off the camera.
    ///
    /// Dynamic Resolution: the buffer holds no size. Its copy is a (-1, -1) temporary (the size of the
    /// target the camera draws into at that moment), it draws into the camera's own target, and it keeps
    /// nothing between renders, so a camera drawn twice a frame, or into another mod's target of another
    /// size, is simply drawn over again right. It hangs on the GAME's camera (GameCamera), which such a
    /// mod renders by hand: command buffers run then too.
    ///
    /// Another mod with a wet look of its own: VW publishes the global _VWWetDrawn (x 1 while this draws, y the
    /// wetness drawn) for it to stand down; VW reads nothing back. Its buffer goes after the ones already
    /// at the event, so it wets a picture that has theirs in it.
    /// </remarks>
    public class WetGround : MonoBehaviour
    {
        private const string BufferName = "VolumetricClouds wet ground";
        private const CameraEvent Event = CameraEvent.AfterFinalPass;

        /// <summary>Above this over the ground the wet look is lost in the distance: nothing runs.</summary>
        private const float HighestCamera = 1500f;

        private static readonly int IdScene = Shader.PropertyToID("_VWWetScene");
        private static readonly int IdWet = Shader.PropertyToID("_VWWet");
        private static readonly int IdSun = Shader.PropertyToID("_VWWetSun");
        private static readonly int IdSunDir = Shader.PropertyToID("_VWWetSunDir");
        private static readonly int IdSky = Shader.PropertyToID("_VWWetSky");
        private static readonly int IdHorizon = Shader.PropertyToID("_VWWetHorizon");
        private static readonly int IdHue = Shader.PropertyToID("_VWWetHue");
        private static readonly int IdPublished = Shader.PropertyToID("_VWWetDrawn");

        private CloudDensityField _field;
        private Material _material;
        private Mesh _quad;
        private CommandBuffer _buffer;
        private Camera _attachedTo;
        private bool _missingLogged;
        private string _loggedState;
        private float _nextLog;
        private double _clock;

        /// <summary>The wetness drawn this frame (0 = nothing), for the log and other parts.</summary>
        public static float Drawn { get; private set; }

        /// <summary>The pass is drawing and needs TerrainHeightMap's water heights (the seabed stays dry).</summary>
        public static bool WantsWaterMap { get; private set; }

        private static readonly int IdWater = Shader.PropertyToID("_VWWetWater");
        private static readonly int IdWaterOn = Shader.PropertyToID("_VWWetWaterOn");
        private static readonly int IdPlaces = Shader.PropertyToID("_VWWetPlaces");

        /// <summary>The ground's wetness as the game has it, 0..1 (0 with no weather manager).</summary>
        public static float GroundWetness
        {
            get { return Singleton<WeatherManager>.exists ? Mathf.Clamp01(Singleton<WeatherManager>.instance.m_groundWetness) : 0f; }
        }

        public void Initialise(CloudDensityField field)
        {
            _field = field;
        }

        private static float Row
        {
            get { return Settings.WetGround != null ? Mathf.Clamp(Settings.WetGround.value, 0f, 2f) : Settings.Defaults.WetGround; }
        }

        private void LateUpdate()
        {
            float row = Row;
            Camera camera = GameCamera.Get();
            float wetness = GroundWetness;

            string why = null;
            if (row <= 0f) why = "the row is at 0%";
            else if (!Settings.WeatherShown) why = "the weather is hidden";
            else if (CloudRain.IsSnow) why = "a winter map (snow, not rain)";
            else if (wetness <= 0.01f) why = "the ground is dry";
            else if (camera == null) why = "no camera";
            else if (HeightAboveGround(camera) > HighestCamera) why = "the camera is high over the city";
            else if (InfoView()) why = "an info view is open";
            else if (!EnsureMaterial()) why = "its shader is not in this bundle";

            if (why != null)
            {
                Detach(why);
                Drawn = 0f;
                WantsWaterMap = false;
                Shader.SetGlobalVector(IdPublished, Vector4.zero);
                return;
            }

            Attach(camera);

            float drawn = Mathf.Clamp01(wetness * row);
            Drawn = drawn;
            WantsWaterMap = true;
            UpdateMaterial(drawn);

            // Under water stays dry: the seabed would be darkened and mirrored under the water's surface.
            bool water = TerrainHeightMap.Ready && TerrainHeightMap.Texture != null && TerrainHeightMap.Places != null;
            if (water)
            {
                _material.SetTexture(IdWater, TerrainHeightMap.Texture);
                _material.SetTexture(IdPlaces, TerrainHeightMap.Places);
            }
            _material.SetFloat(IdWaterOn, water ? 1f : 0f);
            Shader.SetGlobalVector(IdPublished, new Vector4(1f, drawn, 1f, 0f));

            if (Log.Detailed && Time.time >= _nextLog)
            {
                _nextLog = Time.time + 5f;
                Log.Detail("wet ground: wetness " + (wetness * 100f).ToString("F0") + "% x row " + (row * 100f).ToString("F0") +
                           "% = " + (drawn * 100f).ToString("F0") + "%, puddles on up to " + (PuddleShare(drawn) * 100f).ToString("F0") +
                           "% of level hard ground, rain at the camera " + CloudRain.LocalRain(camera.transform.position).ToString("F2"));
            }
        }

        /// <summary>An info view is open (the game's own colours: no weather on them).</summary>
        private static bool InfoView()
        {
            return Singleton<InfoManager>.exists && Singleton<InfoManager>.instance.CurrentMode != InfoManager.InfoMode.None;
        }

        private static float HeightAboveGround(Camera camera)
        {
            Vector3 p = camera.transform.position;
            float ground = Singleton<TerrainManager>.exists ? Singleton<TerrainManager>.instance.SampleRawHeightSmooth(p) : 0f;
            return p.y - ground;
        }

        /// <summary>The share of level hard ground puddles may cover: 35% at 100%, more above.</summary>
        public static float PuddleShare(float drawn)
        {
            return Mathf.Clamp01(0.35f * Mathf.Max(1f, Row) * Mathf.Clamp01(drawn * 1.5f));
        }

        private bool EnsureMaterial()
        {
            if (_material != null)
                return true;

            // Looked up once: ShaderBundle.Get warns on every call that finds nothing.
            if (_missingLogged)
                return false;
            Shader shader = ShaderBundle.Get(ShaderBundle.WetGround);
            if (shader == null)
            {
                _missingLogged = true;
                Log.Msg("wet ground: NOT AVAILABLE here (its shader is not in this platform's bundle or not supported); the game's own wet roads stay");
                return false;
            }

            _material = new Material(shader) { name = "VolumetricCloudsWetGround" };
            _quad = FullScreenQuad();
            return true;
        }

        private void Attach(Camera camera)
        {
            if (_attachedTo == camera && _buffer != null)
                return;

            Detach(null);

            _buffer = new CommandBuffer { name = BufferName };
            _buffer.GetTemporaryRT(IdScene, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR, RenderTextureReadWrite.Linear);
            _buffer.Blit(BuiltinRenderTextureType.CameraTarget, IdScene);
            _buffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            _buffer.DrawMesh(_quad, Matrix4x4.identity, _material, 0, 0);
            _buffer.ReleaseTemporaryRT(IdScene);

            // The names of the buffers already there, once: what this pass sees under it.
            CommandBuffer[] there = camera.GetCommandBuffers(Event);
            string names = "";
            foreach (CommandBuffer b in there)
                names += (names.Length > 0 ? ", " : "") + "'" + b.name + "'";

            camera.AddCommandBuffer(Event, _buffer);
            _attachedTo = camera;
            _loggedState = null;

            if (camera.actualRenderingPath != RenderingPath.DeferredLighting && camera.actualRenderingPath != RenderingPath.DeferredShading)
                Log.Msg("wet ground: the camera draws " + camera.actualRenderingPath + ": no final pass, nothing is drawn");

            Log.Msg("wet ground: on -- on '" + camera.name + "' after its final pass" +
                    (there.Length > 0 ? ", behind " + there.Length + " other buffer(s) there: " + names : "") +
                    "; reflections " + (TraceLevel() > 0 ? "traced (" + (TraceLevel() > 1 ? 32 : 20) + " steps)" : "sky only (Low quality)"));
        }

        private void Detach(string why)
        {
            if (_buffer == null)
                return;

            if (_attachedTo != null)
                _attachedTo.RemoveCommandBuffer(Event, _buffer);
            _buffer.Release();
            _buffer = null;
            _attachedTo = null;

            if (why != null && why != _loggedState)
            {
                _loggedState = why;
                Log.Msg("wet ground: off -- " + why);
            }
        }

        /// <summary>0 sky only (Low), 1 traced 20 steps (Medium), 2 traced 32 steps (High and Custom).</summary>
        private static int TraceLevel()
        {
            int preset = Settings.QualityPreset != null ? Settings.QualityPreset.value : Settings.Defaults.Preset;
            return preset <= 0 ? 0 : preset == 1 ? 1 : 2;
        }

        private void UpdateMaterial(float drawn)
        {
            // SampleRain for the rings: where it rains, the wind, the cloud base.
            if (_field != null)
                CloudShaderParams.Apply(_material, _field, null);

            SimulationManager simulation = Singleton<SimulationManager>.exists ? Singleton<SimulationManager>.instance : null;
            bool paused = simulation == null || simulation.SimulationPaused || simulation.ForcedSimulationPaused;
            if (!paused)
                _clock = (_clock + Time.deltaTime * Mathf.Min(2, Mathf.Max(1, simulation.FinalSimulationSpeed))) % 3600.0;

            _material.SetVector(IdWet, new Vector4(drawn, PuddleShare(drawn) / Mathf.Max(Mathf.Clamp01(drawn * 1.5f), 1e-3f),
                                                   (float)_clock, TraceLevel()));

            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            DayNightProperties properties = DayNightProperties.instance;
            Light sun = properties != null && properties.m_SunLight != null ? properties.m_SunLight.GetComponent<Light>() : null;
            Vector3 toSun = sun != null ? -sun.transform.forward : Vector3.up;
            Color sunColour = sun != null ? (linear ? sun.color.linear : sun.color) * sun.intensity : Color.black;
            float clear = 1f - Mathf.Clamp01(CloudWeather.Coverage);
            float glint = clear * clear * Mathf.Clamp01(toSun.y * 4f);
            _material.SetVector(IdSun, new Vector4(sunColour.r, sunColour.g, sunColour.b, glint));
            _material.SetVector(IdSunDir, toSun);

            Color sky = linear ? RenderSettings.ambientSkyColor.linear : RenderSettings.ambientSkyColor;
            Color horizon = linear ? RenderSettings.ambientEquatorColor.linear : RenderSettings.ambientEquatorColor;
            float rings = 1f;
            _material.SetVector(IdSky, new Vector4(sky.r, sky.g, sky.b, rings));
            _material.SetVector(IdHorizon, new Vector4(horizon.r * 1.2f, horizon.g * 1.2f, horizon.b * 1.2f, 250f));

            // The hue a colour is judged against: the sun's by day, the city's lamps (warm) by night.
            float night = Mathf.Clamp01(-toSun.y * 6f + 0.3f);
            Color day = sunColour.maxColorComponent > 1e-4f ? sunColour / sunColour.maxColorComponent : Color.white;
            Color hue = Color.Lerp(day, new Color(1f, 0.82f, 0.6f), night);
            _material.SetVector(IdHue, new Vector4(hue.r, hue.g, hue.b, 0f));

            if (_loggedState != "drawn")
            {
                _loggedState = "drawn";
                Log.Msg("wet ground: wet " + (drawn * 100f).ToString("F0") + "% (the game's ground wetness " +
                        (GroundWetness * 100f).ToString("F0") + "% x the row), puddles on up to " +
                        (PuddleShare(drawn) * 100f).ToString("F0") + "% of level hard ground");
            }
        }

        private static Mesh FullScreenQuad()
        {
            Mesh mesh = new Mesh { name = "VolumetricCloudsWetQuad" };
            mesh.vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            return mesh;
        }

        private void OnDestroy()
        {
            Detach("the city closes");
            Shader.SetGlobalVector(IdPublished, Vector4.zero);
            if (_material != null)
                Destroy(_material);
            if (_quad != null)
                Destroy(_quad);
        }
    }
}
