using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// Renders real cloud shadows: a texture where each texel is one sun ray marched through
    /// the cloud volume, assigned as the sun's light cookie.
    /// </summary>
    /// <remarks>
    /// Unity projects a directional cookie in the light's local XY plane, centred on the
    /// light's position and repeating every cookieSize metres. So the light is pinned to
    /// <see cref="Anchor"/> and the cookie made large enough to cover the whole map in a
    /// single tile -- the pattern isn't periodic in light space, so a visible repeat would
    /// be a seam.
    /// </remarks>
    public class CloudShadowMap
    {
        /// <summary>Where the sun's transform is pinned while the shadow map is in use (map centre).</summary>
        public static readonly Vector3 Anchor = Vector3.zero;

        /// <summary>
        /// The map is 17.28 km square, so its corners are 12.2 km from the centre; one tile
        /// has to reach them at any sun azimuth.
        /// </summary>
        public const float CookieSize = 26000f;

        /// <summary>
        /// Metres of the slab's height a step of the map's march takes (1.6.1). Until 1.6.0 the march took 20 steps
        /// whatever the slab: 40 m a step for Classic's 800 m, but ~145 m for the ~2.9 km slab of tall heaps (Cumulus
        /// height, Cloud variety), and the shadows of soft and heap skies came out in blocks. Now the count follows the
        /// slab so a step is about as tall as Classic's always was. Fields, so the bench can try others.
        /// </summary>
        public static float StepHeight = 40f;

        /// <summary>Never fewer than 1.6.0's 20: Classic at its thickness (800 m or less) marches exactly as before.</summary>
        public static int FewestSteps = 20;

        /// <summary>
        /// The most steps a texel's ray takes: CloudShadowMapPass.cginc's loop stops at SHADOW_MOST_STEPS (keep the two
        /// equal; the shader clamps to its own bound, so more here would only be ignored). 48 (measured 2026-11-20 in the
        /// cloud bench, his file, sun 35 deg, docs/probe-2026-11-20): Auto's ~2.9 km slab then takes 60 m of height a step,
        /// which already puts its shadows nearer the fine march than Classic's own 20 steps are (mean error 3.6/255 against
        /// Classic's 6.8; the blocks gone), at 3.6-4.5 ms per update of the 2048 map (1.0-1.9 with 20 steps; 0.5-1.4 vs
        /// 0-0.9 at 1024: ranges, the bench's tick floor hides part of each); 40 m a step (73) cost 5.7-6.6 ms for mean error 2.4.
        /// </summary>
        public static int MostSteps = 48;

        /// <summary>The march's steps for a slab this many metres tall.</summary>
        public static int StepsFor(float slab)
        {
            return Mathf.Clamp(Mathf.CeilToInt(slab / Mathf.Max(1f, StepHeight) - 1e-3f), FewestSteps, Mathf.Max(FewestSteps, MostSteps));
        }

        private int _loggedSteps = -1;

        /// <summary>
        /// The map's size and how often it is re-marched, both from the settings: it is a fixed
        /// GPU cost that does not scale with the screen, so on a weak card it is the first
        /// thing to turn down. Changing either rebuilds the texture.
        /// </summary>
        private static int WantedResolution
        {
            get
            {
                return Settings.ShadowMapResolution != null
                    ? Mathf.Clamp(Mathf.ClosestPowerOfTwo(Settings.ShadowMapResolution.value), 256, 4096)
                    : Settings.Defaults.ShadowMapResolution;
            }
        }

        private static int Rate
        {
            get
            {
                return Settings.ShadowMapRate != null
                    ? Mathf.Clamp(Settings.ShadowMapRate.value, 1, 60)
                    : Settings.Defaults.ShadowMapRate;
            }
        }

        private static float UpdateInterval
        {
            get { return 1f / Rate; }
        }

        /// <summary>
        /// The least time between two draws now (1.6.1): the row's rate, stretched by the steps the map was last marched with
        /// over 20 (ShadowCadence.Interval), so a draw a second costs what it did in 1.6.0. Before the first draw: the row's.
        /// </summary>
        private float DrawInterval
        {
            get
            {
                return (float)ShadowCadence.Interval(Rate, _drawnSteps > 0 ? _drawnSteps : FewestSteps, FewestSteps, _shadowSpeed,
                    CookieSize / Mathf.Max(1, _resolution > 0 ? _resolution : WantedResolution));
            }
        }

        private int _drawnSteps;
        // How fast the wind moved the shadows over the ground between the last two draws (m a second, the game's speed in
        // it: WhyDraw's wind measure over the time between them), and when the last was drawn.
        private double _shadowSpeed;
        private float _drawnAt = -1f;

        private static readonly int IdLightPos = Shader.PropertyToID("_LightPos");
        private static readonly int IdLightRight = Shader.PropertyToID("_LightRight");
        private static readonly int IdLightUp = Shader.PropertyToID("_LightUp");
        private static readonly int IdLightForward = Shader.PropertyToID("_LightForward");
        private static readonly int IdCookieSize = Shader.PropertyToID("_CookieSize");
        private static readonly int IdShadowDepth = Shader.PropertyToID("_ShadowDepth");
        private static readonly int IdShadowFullness = Shader.PropertyToID("_ShadowFullness");
        private static readonly int IdShadowSteps = Shader.PropertyToID("_ShadowSteps");
        private static readonly int IdDetailShadowLod = Shader.PropertyToID("_DetailShadowLod");

        /// <summary>The live shadow map, if the shader was available. Read by CloudLighting.</summary>
        public static CloudShadowMap Current { get; private set; }

        private const float ProbeInterval = 20f;

        private readonly Material _material;
        private RenderTexture _texture;
        private Texture2D _probeTexture;
        private float _nextUpdate;

        // What the map was last drawn from, to skip a draw that would give the same map (a paused city).
        private CloudDensityField _drawnField;
        private double _drawnDriftX = double.NaN, _drawnDriftZ = double.NaN;
        private float _drawnCoverage = -1f;
        private float _forceRedraw;
        private int _skipped;

        /// <summary>Draws skipped because nothing had moved, since the last call (for the detailed log).</summary>
        public int TakeSkipped()
        {
            int n = _skipped;
            _skipped = 0;
            return n;
        }
        private float _nextProbe;
        private int _resolution;
        private Vector3 _drawnForward;
        private bool _everyFrame;

        public RenderTexture Texture
        {
            get { return _texture; }
        }

        /// <summary>True once there is real content to project.</summary>
        public bool IsReady { get; private set; }

        private CloudShadowMap(Shader shader)
        {
            _material = new Material(shader) { name = "VolumetricCloudsShadowMap" };
        }

        public static CloudShadowMap TryCreate()
        {
            // The whole shadow shader or Simpler clouds', as CloudShaders chose with the clouds' (null: none
            // runs here, no cloud shadows -- never a cookie nothing draws into).
            Shader shader = CloudShaders.ShadowMap;
            if (shader == null)
            {
                Log.Msg("cloud shadow map: no shader that runs here; no cloud shadows");
                return null;
            }

            CloudShadowMap map = new CloudShadowMap(shader);
            Current = map;
            int resolution = WantedResolution;
            Log.Msg("cloud shadow map created: " + resolution + "x" + resolution + " over " +
                    CookieSize + " m (" + (CookieSize / resolution).ToString("F1") + " m/texel) at " +
                    (1f / UpdateInterval).ToString("F0") + " Hz");
            return map;
        }

        /// <summary>
        /// How much of the direct light a thick cloud removes, for a given coverage.
        /// </summary>
        /// <remarks>
        /// The shadow map is the *only* thing that darkens the world; there is no separate
        /// global sun dimming. Gaps between clouds get full sun, ground under thick cloud
        /// gets (1 - darkness), and the world darkens with coverage simply because more of
        /// it is in shade.
        ///
        /// Darkness is deliberately the same at every coverage. Earlier versions blended it
        /// towards an "overcast floor" as coverage rose; at the ~97% this mod is actually
        /// played at, that made the strength slider 3% of the result and it felt disconnected.
        /// Before that, a global dim was balanced against the cookie and erased the shadows
        /// at high coverage altogether.
        /// </remarks>
        public static float ShadowDepth(float coverage)
        {
            // With a clock (1.5.0): the darkness as the time of day has it now.
            return Mathf.Clamp01(Settings.Now(Settings.CloudShadowDarkness, Settings.CloudShadowDarknessSunset,
                Settings.CloudShadowDarknessNight, Settings.Defaults.ShadowDarkness));
        }

        /// <summary>
        /// Whether the map says where the sun reaches, for what reads it back (the fog's sun shafts,
        /// the rainbow). It stores 1 - (1 - transmittance) x darkness: at "Shadow darkness" 0% that
        /// is white everywhere, and undone (SunShaft, BowShaft in CloudRaymarch.shader) every point
        /// would read as shaded -- fog with no sun at all, and never a rainbow.
        /// </summary>
        public static bool Readable(float coverage)
        {
            return ShadowDepth(coverage) >= 0.01f;
        }

        /// <summary>Multiplier on optical depth for shadows only; see CloudShadowMap.shader.</summary>
        public static float ShadowFullness
        {
            get
            {
                return Settings.CloudShadowFullness != null
                    ? Mathf.Max(0.01f, Settings.CloudShadowFullness.value)
                    : Settings.Defaults.ShadowFullness;
            }
        }

        public void Render(Light sun, CloudDensityField field, Texture3D noise)
        {
            if (sun == null || field == null || noise == null)
                return;

            Transform t = sun.transform;

            if (Time.time < _nextUpdate && IsReady && !SunMovedShadows(t.forward))
                return;

            // (2026-11-15, performance) Nothing that moves the shadows has moved since the last draw --
            // a paused city: the same drift, sun, cover and pattern -- so drawing again would give the
            // same map: skip it, but still draw once a second, so a slider moved while paused shows.
            // (2026-11-17) "Moved" is now "moved the shadows by an eighth of a texel" (ShadowCadence): the
            // wind, the sun's turn and the cover each measured in what they do to the map since it was drawn;
            // any setting changed, a new pattern or texture, draws at once.
            float coverage = CloudShaderParams.Coverage;
            float depth = ShadowDepth(coverage);
            _nextUpdate = Time.time + DrawInterval;
            string why = IsReady ? WhyDraw(field, noise, t, coverage, depth) : "first";
            if (why == null)
            {
                _skipped++;
                return;
            }
            // The shadows' speed since the last draw (before the drift drawn from is replaced): the half-texel bound's.
            float sinceDraw = Time.time - _drawnAt;
            if (IsReady && _drawnAt >= 0f && sinceDraw > 1e-4f && !double.IsNaN(_drawnDriftX))
                _shadowSpeed = ShadowCadence.DriftShift(CloudWind.X - _drawnDriftX, CloudWind.Z - _drawnDriftZ, CloudWind.ShiftX - _drawnShiftX,
                    CloudWind.ShiftZ - _drawnShiftZ, CloudWind.NoiseDrift) / sinceDraw;
            _drawnAt = Time.time;
            _drawnField = field;
            _drawnVersion = field.Version;
            _drawnNoise = noise;
            _drawnCumulus = CloudStyle.Texture;
            _drawnMix = CloudMixMap.Map;
            _drawnDetail = CloudDetail.Texture;
            _drawnRevision = Setting.CloudRevision;
            _drawnDriftX = CloudWind.X;
            _drawnDriftZ = CloudWind.Z;
            _drawnShiftX = CloudWind.ShiftX;
            _drawnShiftZ = CloudWind.ShiftZ;
            _drawnCoverage = coverage;
            _drawnDepth = depth;
            _drawnFrame = new ShadowCadence.Frame(t.right.x, t.right.y, t.right.z, t.up.x, t.up.y, t.up.z, t.forward.x, t.forward.y, t.forward.z);
            _forceRedraw = Time.time + 1f;
            _drawn++;
            CountReason(why);

            if (!EnsureTexture())
                return;

            _drawnForward = t.forward;

            // The DAY's density (1.5.0): the density's clock is for the clouds you see; the shadows
            // follow "Shadow darkness" and its own clock.
            CloudShaderParams.Apply(_material, field, noise, Settings.CloudDensityDay);
            // The slab the map was drawn through, for the sun's measure next time (WhyDraw).
            _drawnBottom = _material.GetFloat(IdCloudBottom);
            _drawnTop = _material.GetFloat(IdCloudTop);
            // The extras' variant exactly while the cloud material has it (CloudShaders.PlusOn, CloudVolume.ApplyExtras):
            // the shadows fall from the clouds as drawn (Softer cumulus's density). Never on the light shader (it has none).
            if (CloudShaders.PlusOn && !CloudShaders.Lite)
                _material.EnableKeyword(CloudShaders.PlusKeyword);
            else
                _material.DisableKeyword(CloudShaders.PlusKeyword);
            _material.SetVector(IdLightPos, Anchor);
            _material.SetVector(IdLightRight, t.right);
            _material.SetVector(IdLightUp, t.up);
            _material.SetVector(IdLightForward, t.forward);
            _material.SetFloat(IdCookieSize, CookieSize);
            _material.SetFloat(IdShadowDepth, ShadowDepth(CloudShaderParams.Coverage));
            _material.SetFloat(IdShadowFullness, ShadowFullness);
            int steps = StepsFor(_drawnTop - _drawnBottom);
            _drawnSteps = steps;
            _material.SetFloat(IdShadowSteps, steps);
            // The slab moves with the cover under Auto: the first count always in the log, a change only in the detailed one.
            if (steps != _loggedSteps && (_loggedSteps < 0 || Log.Detailed))
            {
                string line = "cloud shadow map: " + steps + " steps through the " + (_drawnTop - _drawnBottom).ToString("F0") + " m the clouds can be in (" +
                        ((_drawnTop - _drawnBottom) / steps).ToString("F0") + " m of height a step; 20 steps up to " +
                        (StepHeight * FewestSteps).ToString("F0") + " m, as before 1.6.1), drawn at most " +
                        (1f / (float)ShadowCadence.Interval(Rate, steps, FewestSteps)).ToString("0.#") + " times a second (the rate row's " + Rate +
                        " x " + FewestSteps + " / the steps: the cost a second of 1.6.0's 20 steps), more often up to the row's rate when the " +
                        "wind would move the shadows over half a texel between two draws";
                if (_loggedSteps < 0)
                    Log.Msg(line);
                else
                    Log.Detail(line);
                _loggedSteps = steps;
            }

            // Cloud detail's billows, averaged over what one texel of this map covers.
            _material.SetFloat(IdDetailShadowLod, CloudShaderParams.DetailLod(CookieSize / _resolution));

            RenderTexture previous = RenderTexture.active;
            Graphics.Blit(null, _texture, _material);
            RenderTexture.active = previous;

            if (!IsReady)
                _nextProbe = Time.time + 3f;

            IsReady = true;

            // The readback is a diagnostic AND it costs a GPU sync, so it runs only while the
            // player has asked for detailed logging. Nothing on screen depends on it.
            if (Log.Detailed && Time.time >= _nextProbe)
            {
                _nextProbe = Time.time + ProbeInterval;
                Probe();
                int skipped = TakeSkipped();
                Log.Detail("cloud shadow map: " + _drawnSinceProbe + " drawn and " + skipped + " skipped since the last probe " +
                           "(skipped: nothing had moved the shadows by an eighth of a texel, " +
                           ShadowCadence.Limit(CookieSize, _resolution).ToString("F1") + " m; drawn for: " + TakeReasons() + ")");
            }
        }

        // ---- what the map was last drawn from (WhyDraw) ----
        private static readonly int IdCloudBottom = Shader.PropertyToID("_CloudBottom");
        private static readonly int IdCloudTop = Shader.PropertyToID("_CloudTop");

        /// <summary>The map's half side: the city's 17.28 km square, whose corners the sun's measure looks at.</summary>
        private const float MapHalf = 8640f;

        /// <summary>
        /// The change of the cover the map may lag by. Measured in the cloud bench (his two settings, every style): a
        /// change of 0.001 moves the map by 0.01-0.24 of a step of 255 on average up to 68% cover, but by 5 near a
        /// closed deck (Auto at 98%), where the layer's cut moves fast with the cover. So this is small: while the cover
        /// moves (the weather changing; it settles exactly on its target) the map is drawn at its rate as before, and
        /// a cover that stands still costs nothing. A field, so the bench can sweep it.
        /// </summary>
        public static float CoverStep = 0.00005f;

        /// <summary>The change of the shadows' darkness that moves no texel by more than 0.3 of a step of 255.</summary>
        private const float DepthStep = 0.3f / 255f;

        private int _drawnVersion = -1;
        private Texture _drawnNoise, _drawnCumulus, _drawnMix, _drawnDetail;
        private int _drawnRevision = -1;
        private double _drawnShiftX, _drawnShiftZ;
        private float _drawnDepth = -1f;
        private float _drawnBottom, _drawnTop;
        private ShadowCadence.Frame _drawnFrame;
        private int _drawn, _drawnSinceProbe;
        private int _forFirst, _forInput, _forWind, _forSun, _forCover, _forSecond;

        /// <summary>Draws since the city started (the bench counts them).</summary>
        public int Drawn
        {
            get { return _drawn; }
        }

        /// <summary>
        /// Why the map must be drawn again now, in a word (null: it need not). A field, pattern or texture swapped, a
        /// setting changed, the wind and the sun moved the shadows by an eighth of a texel together (ShadowCadence), or
        /// the cover or the darkness by as much, or a second has gone by since the last draw.
        /// </summary>
        private string WhyDraw(CloudDensityField field, Texture3D noise, Transform sun, float coverage, float depth)
        {
            // (The field's own textures follow its Version: a new pattern or size bumps it. Their getters make them,
            // so they are not asked here.)
            if (field != _drawnField || field.Version != _drawnVersion || noise != _drawnNoise || CloudStyle.Texture != _drawnCumulus ||
                CloudMixMap.Map != _drawnMix || CloudDetail.Texture != _drawnDetail || Setting.CloudRevision != _drawnRevision)
                return "input";

            // Each as a share of what it may move alone; together they share one budget, so the map's error stays
            // that of one of them at its limit (measured: about 0.3 of a step of 255 on average) whatever moves.
            double limit = ShadowCadence.Limit(CookieSize, _resolution);
            double wind = ShadowCadence.DriftShift(CloudWind.X - _drawnDriftX, CloudWind.Z - _drawnDriftZ, CloudWind.ShiftX - _drawnShiftX,
                CloudWind.ShiftZ - _drawnShiftZ, CloudWind.NoiseDrift) / limit;
            Vector3 r = sun.right, u = sun.up, f = sun.forward;
            var now = new ShadowCadence.Frame(r.x, r.y, r.z, u.x, u.y, u.z, f.x, f.y, f.z);
            double turned = ShadowCadence.SunShift(_drawnFrame, now, MapHalf, _drawnBottom, _drawnTop, SunLowest) / limit;
            double cover = Mathf.Abs(coverage - _drawnCoverage) / CoverStep + Mathf.Abs(depth - _drawnDepth) / DepthStep;
            if (wind + turned + cover >= 1.0)
                return wind >= turned && wind >= cover ? "wind" : turned >= cover ? "sun" : "cover";

            if (Time.time >= _forceRedraw)
                return "second";

            return null;
        }

        private void CountReason(string why)
        {
            _drawnSinceProbe++;
            switch (why)
            {
                case "first": _forFirst++; break;
                case "input": _forInput++; break;
                case "wind": _forWind++; break;
                case "sun": _forSun++; break;
                case "cover": _forCover++; break;
                default: _forSecond++; break;
            }
        }

        private string TakeReasons()
        {
            string s = "wind " + _forWind + ", sun " + _forSun + ", cover " + _forCover + ", settings or textures " + (_forInput + _forFirst) +
                       ", the once-a-second draw " + _forSecond;
            _forFirst = _forInput = _forWind = _forSun = _forCover = _forSecond = _drawnSinceProbe = 0;
            return s;
        }

        /// <summary>
        /// True when the sun has turned far enough since the map was drawn to move the shadows on
        /// the ground by more than one texel. A shadow falls height / tan(elevation) from its
        /// cloud, so a turn of a moves it by about height x a / sin^2(elevation): at 2 deg, with the
        /// game's clock turning the sun ~0.4 deg a second, that is km a second, and a map drawn
        /// 20 times a second showed it as shadows standing still and jumping hundreds of metres
        /// (the flicker at sunset, 2026-10-18). Then the map is drawn every frame instead.
        /// </summary>
        private bool SunMovedShadows(Vector3 forward)
        {
            float sinElevation = -forward.y;
            if (sinElevation < SunLowest)
                return false;

            float height = (Settings.CloudAltitude != null ? Settings.CloudAltitude.value : Settings.Defaults.Altitude) +
                           CloudShaderParams.LayerHeight * 0.5f;
            float turned = Vector3.Angle(_drawnForward, forward) * Mathf.Deg2Rad;
            bool moved = height * turned / (sinElevation * sinElevation) > CookieSize / _resolution;

            if (moved != _everyFrame)
            {
                _everyFrame = moved;
                if (Log.Detailed)
                    Log.Detail("cloud shadow map: " + (moved
                        ? "drawn every frame -- the low sun (" + (Mathf.Asin(sinElevation) * Mathf.Rad2Deg).ToString("F1") + " deg) moves the shadows more than a texel between maps"
                        : "back to at most " + (1f / DrawInterval).ToString("0.#") + " draws a second"));
            }

            return moved;
        }

        /// <summary>Below this sine of the sun's elevation (1.15 deg) nothing casts (CloudShadowMap.shader).</summary>
        private const float SunLowest = 0.02f;

        /// <summary>
        /// Reads a downsampled copy of the map back and logs what is in it. "Shadows are
        /// faint" and "the pass is rendering plain white" look identical on screen, and this
        /// tells them apart: the mean should track 1 - coverage * depth.
        /// </summary>
        private void Probe()
        {
            const int size = 128;

            RenderTexture small = RenderTexture.GetTemporary(size, size, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;

            try
            {
                Graphics.Blit(_texture, small);

                if (_probeTexture == null)
                    _probeTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);

                RenderTexture.active = small;
                _probeTexture.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);

                Color32[] pixels = _probeTexture.GetPixels32();
                int min = 255;
                int max = 0;
                long sum = 0;
                int shadowed = 0;

                for (int i = 0; i < pixels.Length; i++)
                {
                    int a = pixels[i].a;
                    if (a < min)
                        min = a;
                    if (a > max)
                        max = a;
                    if (a < 250)
                        shadowed++;
                    sum += a;
                }

                float coverage = CloudShaderParams.Coverage;
                float expected = 1f - coverage * ShadowDepth(coverage);

                Log.Detail("shadow map readback: light min=" + (min / 255f).ToString("F2") +
                        " max=" + (max / 255f).ToString("F2") +
                        " mean=" + (sum / 255f / pixels.Length).ToString("F2") +
                        " (expected mean ~" + expected.ToString("F2") + ")" +
                        " shadowedTexels=" + (100f * shadowed / pixels.Length).ToString("F0") + "%");
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(small);
            }
        }

        /// <summary>Render textures are lost on a device reset (alt-tab, resolution change).</summary>
        private bool EnsureTexture()
        {
            int resolution = WantedResolution;

            // The player moved the resolution row: throw the old map away and start again.
            if (_texture != null && _resolution != resolution)
            {
                Log.Msg("cloud shadow map: resolution " + _resolution + " -> " + resolution);
                _texture.Release();
                Object.Destroy(_texture);
                _texture = null;
            }

            if (_texture != null && _texture.IsCreated())
                return true;

            if (_texture == null)
            {
                _resolution = resolution;
                _texture = new RenderTexture(resolution, resolution, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "VolumetricCloudsShadowMap",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                    useMipMap = false,
                    anisoLevel = 0,
                };
            }

            IsReady = false;
            return _texture.Create();
        }

        public void Dispose()
        {
            if (Current == this)
                Current = null;

            IsReady = false;

            if (_texture != null)
            {
                _texture.Release();
                Object.Destroy(_texture);
                _texture = null;
            }

            if (_material != null)
                Object.Destroy(_material);

            if (_probeTexture != null)
                Object.Destroy(_probeTexture);
        }
    }
}
