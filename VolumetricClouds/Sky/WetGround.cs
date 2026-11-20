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
    /// never written: invariant 2), x the place's share of it (<see cref="WetMap"/>, 1.6.1: the same rule per
    /// cell with the rain that fell there, so only where it has rained; 1 everywhere with "follow the clouds"
    /// off), x the row. Nothing here touches a game material or _WeatherParams: the game's own slight wet look
    /// stays underneath.
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
    /// wetness drawn: the most anywhere, the game's wetness x the row -- since 1.6.1 a place where it has not rained
    /// is drawn drier than y, but the meaning of x and y is unchanged) for it to stand down; VW reads nothing back. Its buffer goes after the ones already
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
        private static readonly int IdTerrainMap = Shader.PropertyToID("_VWWetTerrainMap");
        private static readonly int IdTerrain = Shader.PropertyToID("_VWWetTerrain");
        private static readonly int IdKinds = Shader.PropertyToID("_VWWetKinds");
        private static readonly int IdPlaceMap = Shader.PropertyToID("_VWWetPlaceMap");

        private readonly WetTerrainMap _terrain = new WetTerrainMap();

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

        private static float TerrainRow
        {
            get { return Settings.WetTerrain != null ? Mathf.Clamp(Settings.WetTerrain.value, 0f, 2f) : Settings.Defaults.WetTerrain; }
        }

        private static float TreesRow
        {
            get { return Settings.WetTrees != null ? Mathf.Clamp(Settings.WetTrees.value, 0f, 2f) : Settings.Defaults.WetTrees; }
        }

        private void LateUpdate()
        {
            float row = Row;
            float terrainRow = TerrainRow;
            float treesRow = TreesRow;
            Camera camera = GameCamera.Get();
            float wetness = GroundWetness;

            // Where it has rained (1.6.1): runs whatever the camera does (high up, an info view), while anything reads it.
            WetMap.Update(row > 0f || terrainRow > 0f || treesRow > 0f || WheelSpray.Row > 0f);

            string why = null;
            if (row <= 0f && terrainRow <= 0f && treesRow <= 0f) why = "the three wet rows are at 0%";
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
            UpdateMaterial(drawn, wetness * row);

            // Where it has rained (WetMap): each kind's wetness and the puddles' share are worked out per pixel from the
            // map's share (WetGround.shader WetHere); white = the game's wetness everywhere (the look of 1.6.0).
            Texture places = WetMap.Texture;
            _material.SetTexture(IdPlaceMap, places != null ? places : (Texture)Texture2D.whiteTexture);

            // Under water stays dry: the seabed would be darkened and mirrored under the water's surface.
            bool water = TerrainHeightMap.Ready && TerrainHeightMap.Texture != null && TerrainHeightMap.Places != null;
            if (water)
            {
                _material.SetTexture(IdWater, TerrainHeightMap.Texture);
                _material.SetTexture(IdPlaces, TerrainHeightMap.Places);
            }
            _material.SetFloat(IdWaterOn, water ? 1f : 0f);

            // Bare terrain and trees each by their own row (Wet grass and soil, Wet trees): where the camera looks,
            // the game's own surface tells terrain from roads (WetTerrainMap), the normals' alpha trees from the rest.
            // Until the map's first build is in, terrain is wet as a road.
            CameraController controller = GameCamera.Controller;
            _terrain.Update(controller != null ? controller.m_currentPosition : camera.transform.position);
            Texture terrainMap = _terrain.Texture;
            if (terrainMap != null)
                _material.SetTexture(IdTerrainMap, terrainMap);
            _material.SetVector(IdTerrain, new Vector4(_terrain.Centre.x, _terrain.Centre.y, _terrain.BaseHeight, terrainMap != null ? 1f : 0f));
            float terrainWet = Mathf.Clamp01(wetness * terrainRow);
            float treesWet = Mathf.Clamp01(wetness * treesRow);
            // Unclamped: the shader clamps after the map's share (clamp01(w x row) x share would not be the same).
            _material.SetVector(IdKinds, new Vector4(wetness * terrainRow, wetness * treesRow, 0f, 0f));
            Shader.SetGlobalVector(IdPublished, new Vector4(1f, drawn, 1f, 0f));

            if (Log.Detailed && Time.time >= _nextLog)
            {
                _nextLog = Time.time + 5f;
                Log.Detail("wet ground: wetness " + (wetness * 100f).ToString("F0") + "% x row " + (row * 100f).ToString("F0") +
                           "% = " + (drawn * 100f).ToString("F0") + "% on roads, terrain " + (terrainWet * 100f).ToString("F0") +
                           "%, trees " + (treesWet * 100f).ToString("F0") + "%, puddles on up to " + (PuddleShare(drawn) * 100f).ToString("F0") +
                           "% of level hard ground, rain at the camera " + CloudRain.LocalRain(camera.transform.position).ToString("F2") +
                           (WetMap.Following ? ", the ground under the camera " + (WetMap.At(camera.transform.position) * 100f).ToString("F0") +
                                               "% as wet as the game's number (where it has rained)" : ", the same everywhere (wet map not following)"));
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

        /// <summary>
        /// The share of level hard ground puddles cover: rising with the wetness to 30% from 40% wet at 100%, more
        /// above. 2026-11-18 (his "puddles need to be more obvious"): the shader takes THIS share (until then it
        /// re-curved it and drew none below 30% wet; his rain runs 20-60%).
        /// </summary>
        public static float PuddleShare(float drawn)
        {
            return Mathf.Clamp01(PuddleMost * Mathf.Clamp01(drawn * 2.5f));
        }

        /// <summary>The puddles' share at full wetness (WetGround.shader multiplies it by clamp01(wet here x 2.5) and clamps).</summary>
        private static float PuddleMost
        {
            get { return 0.3f * Mathf.Max(1f, Row); }
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

        /// <summary><paramref name="drawn"/> = clamp01(wetness x row), for the log; <paramref name="unclamped"/> = wetness x row, for the shader.</summary>
        private void UpdateMaterial(float drawn, float unclamped)
        {
            // SampleRain for the rings: where it rains, the wind, the cloud base.
            if (_field != null)
                CloudShaderParams.Apply(_material, _field, null);

            SimulationManager simulation = Singleton<SimulationManager>.exists ? Singleton<SimulationManager>.instance : null;
            bool paused = simulation == null || simulation.SimulationPaused || simulation.ForcedSimulationPaused;
            if (!paused)
                _clock = (_clock + Time.deltaTime * Mathf.Min(2, Mathf.Max(1, simulation.FinalSimulationSpeed))) % 3600.0;

            // x the wetness x the row, unclamped; y the puddles' most share (PuddleShare without its wetness term): the shader
            // takes the map's share at the pixel first, then clamps and curves as PuddleShare does (1.6.1; before, x was
            // clamp01(wetness x row) and y PuddleShare(x), the same where the share is 1).
            _material.SetVector(IdWet, new Vector4(unclamped, PuddleMost, (float)_clock, TraceLevel()));

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
            WetMap.Release("the city closes");
            _terrain.Destroy();
            if (_material != null)
                Destroy(_material);
            if (_quad != null)
                Destroy(_quad);
        }
    }
}
