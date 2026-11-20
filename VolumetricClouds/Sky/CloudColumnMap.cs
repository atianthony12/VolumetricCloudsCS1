using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The cloud column map (1.6.1): the drawn clouds round the camera, column by column, for lightning to put its bolts
    /// in (<see cref="CloudColumns"/> says what is in it and why). Drawn by CloudColumnMap.shader (the clouds' own
    /// SampleDensityLod marched straight up per texel) and read back about once a second, only while lightning can
    /// happen (<see cref="Want"/>). Owned by CloudVolume, next to the shadow map; read through <see cref="Columns"/>.
    /// </summary>
    /// <remarks>
    /// SIZE: <see cref="Size"/>^2 texels over <see cref="Side"/> m round the camera, 94 m a texel. Our strikes land 0.8-7 km
    /// from the camera (LightningPlacement), crawlers run up to 6 km further, travelling storm cells are born 2-12 km
    /// upwind and are up to 6 km across: 24 km (12 km each way) holds every strike near the camera and the near half
    /// of a far cell; past it the old 2D test stands in (CloudLightning). 94 m a texel is finer than a heap (several
    /// hundred metres across) and than the 200 m a crawler's cloud test steps by. Its steps: about 30 m of height each
    /// (32-128), so the underside is known to a few metres (interpolated within a step).
    ///
    /// The shader and its variant follow the shadow map's exactly: the light one under Simpler clouds, VC_PLUS on
    /// exactly while the shadow map has it (CloudShaders.PlusOn), the density the clouds are drawn with NOW
    /// (CloudShaderParams.Apply without a density: CloudDensityNow, not the shadows' day value -- the thresholds 0.1
    /// and 1 are the drawn clouds').
    ///
    /// Missing (an older bundle), not supported, or a readback with an all-zero texel (the driver did not draw it):
    /// <see cref="Columns"/> stays null and lightning keeps its old 2D test -- one log line.
    ///
    /// The readback (ReadPixels) waits for the GPU: once a second during a storm only, never otherwise. Dynamic
    /// Resolution: none of this is screen-sized; its own target is fixed, made once.
    /// </remarks>
    public sealed class CloudColumnMap
    {
        public const int Size = 256;
        public const float Side = 24000f;

        /// <summary>Seconds between two maps while wanted (the clouds move a few metres a second: the lookups follow the drift between).</summary>
        public static float Interval = 1f;

        /// <summary>Metres of the slab's height a step of the march takes, and the most steps (the pass's COLUMN_MOST_STEPS).</summary>
        public static float StepHeight = 30f;
        public const int MostSteps = 128, FewestSteps = 32;

        private static readonly int IdColumnRect = Shader.PropertyToID("_ColumnRect");
        private static readonly int IdColumnSteps = Shader.PropertyToID("_ColumnSteps");
        private static readonly int IdCloudBottom = Shader.PropertyToID("_CloudBottom");
        private static readonly int IdCloudTop = Shader.PropertyToID("_CloudTop");

        /// <summary>The live map, if any (CloudVolume makes it).</summary>
        public static CloudColumnMap Current { get; private set; }

        /// <summary>The columns last read back, or null (none yet, or the map cannot run here: the old 2D test).</summary>
        public static CloudColumns Columns
        {
            get { return Current != null ? Current._columns : null; }
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
        private byte[] _bytes;
        private CloudColumns _columns;
        private float _nextDraw;
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

        /// <summary>Once a frame from CloudVolume: draws and reads the map back when wanted and due.</summary>
        public void Update(CloudDensityField field, Texture3D noise)
        {
            if (_failed || field == null || noise == null)
                return;
            if (Time.time > _wantedUntil)
            {
                // Lightning stopped: let the columns go, so a next storm starts from a fresh map (never a stale one).
                _columns = null;
                return;
            }
            if (Time.time < _nextDraw && _columns != null)
                return;
            _nextDraw = Time.time + Interval;

            Camera camera = GameCamera.Get();
            if (camera == null)
                return;

            if (!Ensure())
                return;

            // Centred on the camera, on the texel grid (so a map drawn a second later lies on the same lattice).
            float texel = Side / Size;
            Vector3 eye = camera.transform.position;
            float cx = Mathf.Round(eye.x / texel) * texel, cz = Mathf.Round(eye.z / texel) * texel;

            CloudShaderParams.Apply(_material, field, noise);
            if (CloudShaders.PlusOn && !CloudShaders.Lite)
                _material.EnableKeyword(CloudShaders.PlusKeyword);
            else
                _material.DisableKeyword(CloudShaders.PlusKeyword);
            float bottom = _material.GetFloat(IdCloudBottom), top = _material.GetFloat(IdCloudTop);
            int steps = Mathf.Clamp(Mathf.CeilToInt((top - bottom) / StepHeight), FewestSteps, MostSteps);
            _material.SetVector(IdColumnRect, new Vector4(cx, cz, Side, CloudShaderParams.DetailLod(texel)));
            _material.SetFloat(IdColumnSteps, steps);
            double driftX = CloudWind.X + CloudWind.ShiftX, driftZ = CloudWind.Z + CloudWind.ShiftZ;

            RenderTexture previous = RenderTexture.active;
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                RenderTexture.active = _target;
                GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
                Graphics.Blit(null, _target, _material);
                RenderTexture.active = _target;
                _read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0, false);
                Color32[] pixels = _read.GetPixels32();
                for (int i = 0; i < pixels.Length; i++)
                {
                    _bytes[i * 4] = pixels[i].r;
                    _bytes[i * 4 + 1] = pixels[i].g;
                    _bytes[i * 4 + 2] = pixels[i].b;
                    _bytes[i * 4 + 3] = pixels[i].a;
                }
            }
            finally
            {
                RenderTexture.active = previous;
            }
            _readMs = clock.Elapsed.TotalMilliseconds;

            CloudColumns columns = CloudColumns.Decode(_bytes, Size, cx, cz, Side, bottom, top, driftX, driftZ);
            if (columns == null)
            {
                _failed = true;
                _columns = null;
                Log.Msg("cloud column map: the graphics driver drew nothing into it (" + SystemInfo.graphicsDeviceType + ", '" +
                        SystemInfo.graphicsDeviceName + "'); lightning places its bolts by the weather map, as before 1.6.1");
                return;
            }

            _columns = columns;
            _drawn++;
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Log.Msg("cloud column map: lightning places its bolts in the drawn clouds -- " + Size + "x" + Size + " columns over " +
                        (Side / 1000f).ToString("F0") + " km round the camera (" + texel.ToString("F0") + " m), " + steps + " steps through " +
                        bottom.ToString("F0") + ".." + top.ToString("F0") + " m, " + (CloudShaders.Lite ? "Simpler clouds' density" : CloudShaders.PlusOn ? "the extras' density" : "the clouds' density") +
                        ", read back every " + Interval.ToString("F1") + " s while lightning can happen (this one took " + _readMs.ToString("F1") +
                        " ms of the main thread); " + columns.Describe());
            }
            else if (Log.Detailed && _drawn % 30 == 0)
            {
                Log.Detail("cloud column map: " + _drawn + " drawn, the last read back in " + _readMs.ToString("F1") + " ms; " + columns.Describe());
            }
        }

        private bool Ensure()
        {
            if (_material == null)
                _material = new Material(CloudShaders.Lite ? _lite : _whole) { name = "VolumetricCloudsColumnMap" };

            if (_target != null && !_target.IsCreated())
            {
                Object.Destroy(_target);
                _target = null;
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
                    return false;
            }
            if (_read == null)
                _read = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true);
            if (_bytes == null)
                _bytes = new byte[Size * Size * 4];
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
