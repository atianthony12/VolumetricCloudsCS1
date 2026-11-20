using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The cloud column map (1.6.1): the drawn clouds over the city and round it, column by column, for lightning to put its bolts
    /// in (<see cref="CloudColumns"/> says what is in it and why). Drawn by CloudColumnMap.shader (the clouds' own
    /// SampleDensityLod marched straight up per texel) and read back only while lightning can happen (<see cref="Want"/>)
    /// and only when the map is no longer true (<see cref="ColumnCadence"/>, never more often than every 6 s). Owned by
    /// CloudVolume, next to the shadow map; read through <see cref="Columns"/>.
    /// </summary>
    /// <remarks>
    /// WHERE (1.6.1, after his log of build 27331: centred on the camera, it was read again for every 3 km the camera went --
    /// zooming, orbiting and panning zoomed out move the eye by kilometres -- 13 of 27 reads): FIXED ON THE CITY,
    /// <see cref="Size"/>^2 texels over <see cref="Side"/> m centred on the world's origin, so the camera never causes a read.
    /// The side is the 17.28 km map and <see cref="Margin"/> each way: from a camera at the map's very edge our strikes land
    /// up to 7 km out (LightningPlacement.MaxDistance), the game's strike looks 0.5 km round for its top (ours 0.25), and
    /// between two reads the drift carries what the map holds up to 1 km (ColumnCadence.CarryLimit) -- 8.5 km of the 8.64.
    /// What falls back to the old 2D test (CloudLightning, as before 1.6.1): a burst's follow-up landing 0.1-1.5 km past an
    /// outermost strike, a crawler's far end (it runs 2-6 km from its strike), a storm cell's strikes 8.64+ km off the map.
    /// 90 m a texel (94 m before) is finer than a heap (several hundred metres across) and than the 200 m a crawler's cloud
    /// test steps by. Its steps: about 30 m of height each (32-128), so the underside is known to a few metres
    /// (interpolated within a step).
    ///
    /// The shader and its variant follow the shadow map's exactly: the light one under Simpler clouds, VC_PLUS on
    /// exactly while the shadow map has it (CloudShaders.PlusOn), the density the clouds are drawn with NOW
    /// (CloudShaderParams.Apply without a density: CloudDensityNow, not the shadows' day value -- the thresholds 0.1
    /// and 1 are the drawn clouds').
    ///
    /// Missing (an older bundle), not supported, or a readback with an all-zero texel (the driver did not draw it):
    /// <see cref="Columns"/> stays null and lightning keeps its old 2D test -- one log line.
    ///
    /// The readback (ReadPixels) waits for the GPU's queued frames: 3.5-86 ms of the main thread, median 69, in the author's
    /// game, when it was read once a second (1.6.1's first build). Unity 5.6 has no asynchronous readback (checked by
    /// reflection: no AsyncGPUReadback or GPU fence in the game's UnityEngine), so it is read as rarely as the map allows. Dynamic
    /// Resolution: none of this is screen-sized; its own target is fixed, made once.
    /// </remarks>
    public sealed class CloudColumnMap
    {
        /// <summary>The city's map is 17.28 km square; the column map reaches this far past each of its edges.</summary>
        public const float Margin = 8640f;

        /// <summary>Texels each way and the side (m), centred on the world's origin (fields, so the bench can try others).</summary>
        public static int Size = 384;
        public static float Side = 17280f + 2f * Margin;

        /// <summary>Metres of the slab's height a step of the march takes, and the most steps (the pass's COLUMN_MOST_STEPS).</summary>
        public static float StepHeight = 30f;
        public const int MostSteps = 128, FewestSteps = 32;

        private static readonly int IdColumnRect = Shader.PropertyToID("_ColumnRect");
        private static readonly int IdColumnSteps = Shader.PropertyToID("_ColumnSteps");
        private static readonly int IdCloudBottom = Shader.PropertyToID("_CloudBottom");
        private static readonly int IdCloudTop = Shader.PropertyToID("_CloudTop");

        /// <summary>The live map, if any (CloudVolume makes it).</summary>
        public static CloudColumnMap Current { get; private set; }

        /// <summary>
        /// The columns last read back while they are true, or null (none yet, the map cannot run here, or something that draws
        /// the clouds changed since: the old 2D test meanwhile).
        /// </summary>
        public static CloudColumns Columns
        {
            get { return Current != null && Current._trusted ? Current._columns : null; }
        }

        private static float _wantedUntil = -1f;

        /// <summary>Lightning can happen (CloudLightning, every frame it can): the map is drawn for the next two seconds.</summary>
        public static void Want()
        {
            _wantedUntil = Time.time + 2f;
        }

        private readonly Shader _whole, _lite;
        private Material _material;
        private RenderTexture _target;
        private Texture2D _read;
        private CloudColumns _columns;
        private bool _failed;
        private bool _loggedFirst;
        private int _drawn;
        private double _readMs;

        private CloudColumnMap(Shader whole, Shader lite)
        {
            _whole = whole;
            _lite = lite;
        }

        /// <summary>From CloudVolume, with the shadow map. Null (and one log line) when this bundle has no column shader.</summary>
        public static CloudColumnMap TryCreate()
        {
            Shader whole = CloudShaders.Lite ? null : ShaderBundle.Get(ShaderBundle.ColumnMap);
            Shader lite = CloudShaders.Lite ? ShaderBundle.Get(ShaderBundle.ColumnMapLite) : null;
            Shader chosen = CloudShaders.Lite ? lite : whole;
            if (chosen == null || !chosen.isSupported)
            {
                Log.Msg("cloud column map: " + (chosen == null ? "no shader for it in this bundle" : "its shader is not supported here") +
                        "; lightning places its bolts by the weather map, as before 1.6.1");
                return null;
            }

            CloudColumnMap map = new CloudColumnMap(whole, lite);
            Current = map;
            return map;
        }

        // What the map was last read from (ColumnCadence: anything here changing makes it untrue at once).
        private CloudDensityField _drawnField;
        private int _drawnVersion = -1;
        private Texture _drawnNoise, _drawnCumulus, _drawnMix, _drawnDetail;
        private int _drawnRevision = -1;
        private bool _drawnPlus;
        private float _drawnCover, _drawnDensity;
        private float _readAt;
        private double _awake;
        private bool _trusted;
        private string _lastWhy;
        private float _lastGap;
        private double _lastAwake;
        private int _forFirst, _forInput, _forWind, _forCover, _forDensity, _forCarried, _forCeiling;

        /// <summary>
        /// Once a frame from CloudVolume: reads the map back when lightning can happen and the map is no longer true
        /// (<see cref="ColumnCadence"/>), never more often than every <see cref="ColumnCadence.MinInterval"/> s.
        /// </summary>
        public void Update(CloudDensityField field, Texture3D noise)
        {
            if (_failed || field == null || noise == null)
                return;

            // Unpaused time since the last read (the ceiling counts only that: a paused city with nothing changing never reads).
            bool paused = !ColossalFramework.Singleton<SimulationManager>.exists ||
                          ColossalFramework.Singleton<SimulationManager>.instance.SimulationPaused ||
                          ColossalFramework.Singleton<SimulationManager>.instance.ForcedSimulationPaused;
            if (!paused)
                _awake += Time.deltaTime;

            if (Time.time > _wantedUntil)
            {
                // Lightning stopped: the map is kept but not used; when lightning comes back, it is judged again first.
                _trusted = false;
                return;
            }

            double driftX = CloudWind.X + CloudWind.ShiftX, driftZ = CloudWind.Z + CloudWind.ShiftZ;
            float cover = CloudShaderParams.Coverage;
            float density = Settings.CloudDensityNow;
            bool input = _columns != null &&
                         (field != _drawnField || field.Version != _drawnVersion || noise != _drawnNoise || CloudStyle.Texture != _drawnCumulus ||
                          CloudMixMap.Map != _drawnMix || CloudDetail.Texture != _drawnDetail || Setting.CloudRevision != _drawnRevision ||
                          (CloudShaders.PlusOn && !CloudShaders.Lite) != _drawnPlus || _columns.Size != Size);
            string why;
            if (_columns == null)
            {
                why = ColumnCadence.Why(false, false, 0, 0, 0, 0, 1, 1, 0);
            }
            else
            {
                // How far the drift carried the map's clouds (its lookups follow it; the shift included), and how far the noises
                // slid through them (the wind alone: a shift moves weather map and noises alike).
                double dx = driftX - _columns.DriftX, dz = driftZ - _columns.DriftZ;
                double wx = CloudWind.X - _drawnWindX, wz = CloudWind.Z - _drawnWindZ;
                why = ColumnCadence.Why(true, input, Time.realtimeSinceStartup - _readAt, _awake,
                    ColumnCadence.NoiseShift(wx, wz, CloudWind.NoiseDrift), cover - _drawnCover, _drawnDensity, density,
                    System.Math.Sqrt(dx * dx + dz * dz));
            }

            if (why == null)
            {
                // True enough (or the floor holds a read back): used, but never across a change of what draws the clouds.
                _trusted = !input;
                return;
            }
            if (input)
                _trusted = false;

            if (!Ensure())
                return;

            // Fixed on the city: centred on the world's origin.
            float texel = Side / Size;
            const float cx = 0f, cz = 0f;

            CloudShaderParams.Apply(_material, field, noise);
            if (CloudShaders.PlusOn && !CloudShaders.Lite)
                _material.EnableKeyword(CloudShaders.PlusKeyword);
            else
                _material.DisableKeyword(CloudShaders.PlusKeyword);
            float bottom = _material.GetFloat(IdCloudBottom), top = _material.GetFloat(IdCloudTop);
            int steps = Mathf.Clamp(Mathf.CeilToInt((top - bottom) / StepHeight), FewestSteps, MostSteps);
            _material.SetVector(IdColumnRect, new Vector4(cx, cz, Side, CloudShaderParams.DetailLod(texel)));
            _material.SetFloat(IdColumnSteps, steps);

            RenderTexture previous = RenderTexture.active;
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            Color32[] pixels;
            try
            {
                RenderTexture.active = _target;
                GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                Graphics.Blit(null, _target, _material);
                RenderTexture.active = _target;
                // The wait: ReadPixels holds the main thread until the GPU has drawn every frame queued before it, and this.
                _read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
                _drawMs = clock.Elapsed.TotalMilliseconds;
                pixels = _read.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
            }
            _readMs = clock.Elapsed.TotalMilliseconds;
            _fetchMs = _readMs - _drawMs;

            // The texels kept as GetPixels32 gave them (a new array each call), checked and counted, turned into metres only
            // where a lookup reads (1.6.1: no copy into bytes, no four float arrays decoded a read).
            CloudColumns columns = CloudColumns.DecodePixels(pixels, Size, cx, cz, Side, bottom, top, driftX, driftZ);
            _decodeMs = clock.Elapsed.TotalMilliseconds - _readMs;
            if (columns == null)
            {
                _failed = true;
                _columns = null;
                Log.Msg("cloud column map: the graphics driver drew nothing into it (" + SystemInfo.graphicsDeviceType + ", '" +
                        SystemInfo.graphicsDeviceName + "'); lightning places its bolts by the weather map, as before 1.6.1");
                return;
            }

            _columns = columns;
            _trusted = true;
            _drawn++;
            _lastGap = _drawn > 1 ? Time.realtimeSinceStartup - _readAt : 0f;
            _lastAwake = _awake;
            _readAt = Time.realtimeSinceStartup;
            _awake = 0.0;
            _lastWhy = why;
            CountReason(why);
            _drawnField = field;
            _drawnVersion = field.Version;
            _drawnNoise = noise;
            _drawnCumulus = CloudStyle.Texture;
            _drawnMix = CloudMixMap.Map;
            _drawnDetail = CloudDetail.Texture;
            _drawnRevision = Setting.CloudRevision;
            _drawnPlus = CloudShaders.PlusOn && !CloudShaders.Lite;
            _drawnCover = cover;
            _drawnDensity = density;
            _drawnWindX = CloudWind.X;
            _drawnWindZ = CloudWind.Z;

            if (!_loggedFirst || Log.Detailed)
            {
                System.Diagnostics.Stopwatch describing = System.Diagnostics.Stopwatch.StartNew();
                string described = columns.Describe();
                double describeMs = describing.Elapsed.TotalMilliseconds;
                string took = "took " + (_readMs + _decodeMs + describeMs).ToString("F1") + " ms of the main thread (draw and ReadPixels, which waits " +
                              "for the GPU: " + _drawMs.ToString("F1") + ", GetPixels32 " + _fetchMs.ToString("F1") + ", decode " +
                              _decodeMs.ToString("F1") + ", this line's numbers " + describeMs.ToString("F2") + ")";
                if (!_loggedFirst)
                {
                    _loggedFirst = true;
                    Log.Msg("cloud column map: lightning places its bolts in the drawn clouds -- " + Size + "x" + Size + " columns over " +
                            (Side / 1000f).ToString("F1") + " km fixed on the city (the map and " + (Margin / 1000f).ToString("F2") + " km round it; " +
                            texel.ToString("F0") + " m a column), " + steps + " steps through " +
                            bottom.ToString("F0") + ".." + top.ToString("F0") + " m, " + (CloudShaders.Lite ? "Simpler clouds' density" : CloudShaders.PlusOn ? "the extras' density" : "the clouds' density") +
                            ". This read " + took + ". Read back only while lightning can happen and only when the map is no longer true -- the " +
                            "clouds' shapes slid " + ColumnCadence.NoiseLimit.ToString("F0") + " m through it, the cover moved " +
                            ColumnCadence.CoverLimit.ToString("F2") + ", the density " + (ColumnCadence.DensityLimit * 100.0).ToString("F0") +
                            "% (together), the drift carried its clouds " + (ColumnCadence.CarryLimit / 1000.0).ToString("F0") +
                            " km, or a setting that reaches the clouds or a texture changed (the camera never) -- never more often than every " +
                            ColumnCadence.MinInterval.ToString("F0") + " s" +
                            (ColumnCadence.Ceiling > 0.0 ? ", and at least every " + ColumnCadence.Ceiling.ToString("F0") + " s of unpaused time" : "") +
                            "; " + described);
                }
                else
                {
                    Log.Detail("cloud column map: read #" + _drawn + " for " + why + ", " + _lastGap.ToString("F0") + " s after the last (" +
                               _lastAwake.ToString("F0") + " s unpaused), " + took + "; read for: first " + _forFirst +
                               ", settings or textures " + _forInput + ", wind " + _forWind + ", cover " + _forCover + ", density " + _forDensity +
                               ", clouds carried off " + _forCarried + ", ceiling " + _forCeiling + "; " + described);
                }
            }
        }

        private double _drawMs, _fetchMs, _decodeMs;
        private double _drawnWindX, _drawnWindZ;

        private void CountReason(string why)
        {
            switch (why)
            {
                case "first": _forFirst++; break;
                case "input": _forInput++; break;
                case "wind": _forWind++; break;
                case "cover": _forCover++; break;
                case "density": _forDensity++; break;
                case "carried": _forCarried++; break;
                default: _forCeiling++; break;
            }
        }

        private bool Ensure()
        {
            if (_material == null)
                _material = new Material(CloudShaders.Lite ? _lite : _whole) { name = "VolumetricCloudsColumnMap" };

            if (_target != null && (!_target.IsCreated() || _target.width != Size))
            {
                _target.Release();
                Object.Destroy(_target);
                _target = null;
            }
            if (_read != null && _read.width != Size)
            {
                Object.Destroy(_read);
                _read = null;
            }
            if (_target == null)
            {
                _target = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                {
                    name = "VolumetricCloudsColumnMap",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    useMipMap = false,
                };
                if (!_target.Create())
                {
                    // Once, not a new target every frame (out of video memory, say).
                    _failed = true;
                    Object.Destroy(_target);
                    _target = null;
                    Log.Msg("cloud column map: its " + Size + "x" + Size + " target could not be made; lightning places its bolts by " +
                            "the weather map, as before 1.6.1");
                    return false;
                }
            }
            if (_read == null)
                _read = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            return true;
        }

        public void Dispose()
        {
            if (Current == this)
                Current = null;
            _columns = null;
            if (_material != null)
                Object.Destroy(_material);
            if (_target != null)
            {
                _target.Release();
                Object.Destroy(_target);
            }
            if (_read != null)
                Object.Destroy(_read);
            _material = null;
            _target = null;
            _read = null;
        }
    }
}
