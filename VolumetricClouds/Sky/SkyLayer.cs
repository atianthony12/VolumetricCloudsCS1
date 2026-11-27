using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// THE SKY LAYER (1.5.0): photographed skies behind the clouds -- cirrus, sheep clouds, far cumulus,
    /// a sky of several layers -- as Cities: Skylines II draws its distant clouds (Unity HDRP's Cloud
    /// Layer): a picture of the whole sky per kind, a slider each, and they add up. The pictures are cut
    /// out of CC0 photographs (Poly Haven) by tools/make-skylayer.ps1 and embedded as grey JPEGs
    /// (Resources/SkyLayer, 4096x1024 each since 2026-10-13 -- 2048 was "too blurry" -- ~4.7 MB for all
    /// eight at quality 85; the author's cap is the whole mod around 5 MB, "we have some leeway").
    /// </summary>
    /// <remarks>
    /// A child of the cloud box with its own shader (SkyLayer.shader) and queue: after the game's stars,
    /// before the lightning bolt and the clouds, so everything else in the sky lays itself over it. Lit
    /// with the clouds' own light, copied from their material each frame (their brightness and tints
    /// included, invariant 8), and at night by the glow under the clouds as well (2026-10-18, the author:
    /// "I don't see them at all at night"). Nothing of it exists until a row is above 0: then the eight pictures are
    /// decoded once (two RGBA textures, ~45 MB of graphics memory with mips) and kept for the session.
    /// The pictures are turned per city (its seed), and carried by the wind at half the volumetric
    /// clouds' speed (2026-10-13; first built as HDRP's distortion, a little way along the wind and
    /// back, which read as still): two copies take turns, each carried for a life, then the other comes
    /// in turned another way, so the sky travels and slowly changes. Not touched: rain, lightning, the
    /// rainbow, the shadows.
    /// <para>ONE PICTURE AT A TIME (2026-11-25, <see cref="SkyLayerMix"/>): each life shows one picture per
    /// kind and the rows cut it (thin parts first); the copies are never averaged, they swap.</para>
    /// </remarks>
    public sealed class SkyLayer
    {
        /// <summary>After the game's stars (2503), before the bolt (the clouds' queue - 10) and the clouds (2520 or 3100).</summary>
        public const int Queue = 2507;

        /// <summary>
        /// The distant clouds move at this share of the wind that carries the volumetric clouds
        /// (2026-10-13, the author: "maybe at half the movement speed of the volumetric clouds").
        /// </summary>
        private const double Speed = 0.5;

        /// <summary>How high the layer the pictures lie on is taken to be (m): overhead they move like a cloud this high.</summary>
        private const float LayerHeight = 2000f;

        /// <summary>
        /// Metres of travel a life lasts: one copy shows its pictures this far, then the other takes over
        /// (SkyLayerMix.Presence: the last SwapShare of it is the swap). 6 km (8 until 2026-11-25): carried
        /// -3.45 .. +3.45 km about the picture as photographed (SkyLayerMix.CarrySpan), short of the ~4 km where
        /// the flat layer's stretch begins to show (Phase 1 probe).
        /// </summary>
        private const double Lifetime = 6000.0;

        /// <summary>
        /// Dynamic distant clouds' clock (SkyLayerMix.Dynamic counts its spells and waves in these): the old
        /// Lifetime, kept when that changed so the spells last as they did.
        /// </summary>
        private const double SpellLength = 8000.0;

        /// <summary>
        /// false (2026-11-25, his look in game): the slider fades the one picture. true = the Phase 1 cover cut
        /// (thin parts first, cores last), kept for the bench (set:SkyLayer.CoverCut=true) and Phase 2's re-cut.
        /// </summary>
        internal static bool CoverCut = false;

        /// <summary>A drift step longer than this in one frame is a jump (a save read, a reset), not wind.</summary>
        private const double FlowJump = 2000.0;

        /// <summary>
        /// The cloud cover over which the volumetric clouds' distance fade comes to hold the pictures back
        /// fully (the shader's BehindClouds, 2026-10-14): from none at the first, all of it at the second.
        /// The pictures cannot see where the clouds are, so they fade in the gaps too: under a few clouds
        /// that would only take the distant clouds off the horizon.
        /// </summary>
        private const float BehindFrom = 0.1f;
        private const float BehindFull = 0.5f;

        // Each kind: how high it stands (m), how much its own cloud shades it (the shader's depth towards the
        // sun is an average, 0..1), its forward scattering, its greatest opacity. 2026-10-13, the author: at
        // 100% "super dense and really dark" -- the shade was a sum of four looks (up to 12 of depth: 2% of
        // the sun) and the thickest pictures (far-2, sheep-2: half the sky, 28% of it opaque) hid it all.
        private static readonly Vector4[] KindLight =
        {
            new Vector4(9000f, 0.6f, 0.7f, 0.9f),   // cirrus: thin ice, bright round the sun
            new Vector4(4500f, 1.5f, 0.55f, 0.75f), // sheep clouds
            new Vector4(1500f, 2f, 0.5f, 0.75f),    // far clouds: cumulus with grey bellies
            new Vector4(3000f, 1.5f, 0.55f, 0.8f),  // cloud layers
        };

        private static readonly int IdPicturesA = Shader.PropertyToID("_SkyPicturesA");
        private static readonly int IdPicturesB = Shader.PropertyToID("_SkyPicturesB");
        private static readonly int IdWeights = Shader.PropertyToID("_SkyWeights");
        private static readonly int IdWeights1 = Shader.PropertyToID("_SkyWeights1");
        private static readonly int IdCut0 = Shader.PropertyToID("_SkyCut0");
        private static readonly int IdCut1 = Shader.PropertyToID("_SkyCut1");
        private static readonly int IdKinds = Shader.PropertyToID("_SkyKinds");
        private static readonly int IdCopy0 = Shader.PropertyToID("_SkyCopy0");
        private static readonly int IdCopy1 = Shader.PropertyToID("_SkyCopy1");
        private static readonly int IdFlow = Shader.PropertyToID("_SkyFlow");
        private static readonly int IdSunDir = Shader.PropertyToID("_SunDir");
        private static readonly int IdSunColor = Shader.PropertyToID("_SunColor");
        private static readonly int IdAmbientColor = Shader.PropertyToID("_AmbientColor");
        private static readonly int IdSunsetEdge = Shader.PropertyToID("_SunsetEdge");
        private static readonly int IdNightGlow = Shader.PropertyToID("_NightGlow");
        private static readonly int IdCurveFade = Shader.PropertyToID("_CurveFade");
        private static readonly int[] IdFade =
        {
            Shader.PropertyToID("_CloudBottom"), Shader.PropertyToID("_MaxDistance"), Shader.PropertyToID("_CloudExtent"),
            Shader.PropertyToID("_EdgeStart"), Shader.PropertyToID("_NightOpacity"),
        };
        private float _behind;

        private readonly Material _material;
        private readonly GameObject _holder;
        private readonly MeshRenderer _renderer;
        private Texture2D _picturesA;
        private Texture2D _picturesB;
        private bool _loadFailed;

        private readonly float[] _amounts = new float[SkyLayerMix.Kinds];
        private readonly Vector4[][] _weights = { new Vector4[2 * SkyLayerMix.Kinds], new Vector4[2 * SkyLayerMix.Kinds] };

        // Each picture's histogram for the cut (SkyLayerMix.Histogram), made when the pictures are decoded.
        private readonly float[][] _hist = new float[SkyLayerMix.Pictures.Length][];

        // The motion: how far the wind has carried the distant clouds this session (m, along its path),
        // where they are (m, x and z: Speed x the wind's drift), and per copy where it started, in which
        // life (a new life = a new turn and new pictures), how present it is, its picture and cut per kind.
        private double _travel;
        private double _lastX;
        private double _lastZ;
        private bool _haveLast;
        private double _windX = 1.0;   // which way the distant clouds last moved (unit; +x until they first do)
        private double _windZ;
        private readonly double[] _anchorX = new double[2];
        private readonly double[] _anchorZ = new double[2];
        private readonly long[] _life = { long.MinValue, long.MinValue };
        private readonly float[] _presence = new float[2];
        private readonly int[][] _picture = { new int[SkyLayerMix.Kinds], new int[SkyLayerMix.Kinds] };
        private readonly float[][] _cut = { new float[SkyLayerMix.Kinds], new float[SkyLayerMix.Kinds] };
        private readonly float[][] _cutShare = { new[] { -1f, -1f, -1f, -1f }, new[] { -1f, -1f, -1f, -1f } };
        private readonly int[][] _cutPicture = { new[] { -1, -1, -1, -1 }, new[] { -1, -1, -1, -1 } };

        /// <summary>
        /// Where the dynamic mix starts this session, in lives: random, so a city does not open on the same
        /// spell every time (the travel is not saved). Some loads open with distant clouds, most without.
        /// </summary>
        private readonly double _mixStart = new System.Random().NextDouble() * 1000.0;

        private string _logged;
        private bool _loggedOn;
        private bool _loggedDynamic;

        public SkyLayer(Shader shader, Transform parent, Mesh box)
        {
            _material = new Material(shader) { name = "VolumetricCloudsSkyLayer", renderQueue = Queue };
            _material.SetVectorArray(IdKinds, KindLight);
            _material.SetVectorArray(IdWeights, _weights[0]);
            _material.SetVectorArray(IdWeights1, _weights[1]);

            _holder = new GameObject("VolumetricCloudsSkyLayer");
            _holder.transform.SetParent(parent, false);
            _holder.AddComponent<MeshFilter>().sharedMesh = box;
            _renderer = _holder.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = _material;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _renderer.enabled = false;
        }

        /// <summary>"Dynamic distant clouds" (1.5.0): the mod mixes the kinds itself and the four sliders are not read.</summary>
        public static bool Dynamic
        {
            get { return Settings.DynamicDistantClouds != null ? Settings.DynamicDistantClouds.value : Settings.Defaults.DynamicDistantClouds; }
        }

        /// <summary>
        /// The amounts, 0..1 (cirrus, sheep clouds, far clouds, cloud layers): the rows', or with
        /// <see cref="Dynamic"/> the mix for the cloud cover now, at how far the sky has travelled (so
        /// its spells stop with the game).
        /// </summary>
        private void ReadAmounts()
        {
            if (Dynamic)
            {
                SkyLayerMix.Dynamic(_mixStart + _travel / SpellLength, SkyPattern.FieldSeed, CloudShaderParams.Coverage, _amounts);
                // Its amounts were tuned for the old walk through the pictures: the same mean cloud as then.
                for (int k = 0; k < SkyLayerMix.Kinds; k++)
                    _amounts[k] = SkyLayerMix.ConvertOld(k, _amounts[k]);
                return;
            }

            _amounts[SkyLayerMix.Cirrus] = Amount(Settings.SkyCirrus);
            _amounts[SkyLayerMix.Sheep] = Amount(Settings.SkySheepClouds);
            _amounts[SkyLayerMix.Far] = Amount(Settings.SkyFarClouds);
            _amounts[SkyLayerMix.Layers] = Amount(Settings.SkyCloudLayers);
        }

        private static float Amount(FloatSetting setting)
        {
            return setting != null ? Mathf.Clamp01(setting.value) : 0f;
        }

        /// <summary>
        /// Once a frame, after the cloud material has this frame's light (CloudVolume.UpdateMaterial).
        /// <paramref name="ambient"/> is the pictures' sky light (CloudVolume.ApplyLighting: the clouds',
        /// with the night's floor under Cloud brightness). <paramref name="clouds"/> null, or every row at
        /// 0: nothing is drawn.
        /// </summary>
        public void Update(Material clouds, Vector4 ambient)
        {
            AdvanceFlow();
            AdvanceLives();
            ReadAmounts();
            bool any = false;
            for (int k = 0; k < SkyLayerMix.Kinds; k++)
                any |= _amounts[k] > 0f;

            if (clouds == null || !any)
            {
                LogState(false);
                _renderer.enabled = false;
                return;
            }

            if (_picturesA == null && !_loadFailed)
                Load();
            if (_loadFailed)
            {
                LogState(false);
                _renderer.enabled = false;
                return;
            }

            _material.SetTexture(IdPicturesA, _picturesA);
            _material.SetTexture(IdPicturesB, _picturesB);

            // The two copies: how far each has been carried, its turn, its pictures and their cuts.
            bool show0, show1;
            _material.SetVector(IdCopy0, Copy(0, out show0));
            _material.SetVector(IdCopy1, Copy(1, out show1));
            LogState(true);
            _material.SetVectorArray(IdWeights, _weights[0]);
            _material.SetVectorArray(IdWeights1, _weights[1]);
            _material.SetVector(IdCut0, new Vector4(_cut[0][0], _cut[0][1], _cut[0][2], _cut[0][3]));
            _material.SetVector(IdCut1, new Vector4(_cut[1][0], _cut[1][1], _cut[1][2], _cut[1][3]));
            _behind = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(BehindFrom, BehindFull, CloudShaderParams.Coverage));
            _material.SetVector(IdFlow, new Vector4(show0 ? 1f : 0f, show1 ? 1f : 0f, LayerHeight, _behind));

            // The clouds' own light (CloudVolume.ApplyLighting set it this frame), and their distance fade.
            // Without the Soft style's own light level (CloudStyle.CloudLightScale): the pictures are lit
            // as under every other style. Their glow at night is the clouds' (0 by day), "Glow under the
            // clouds at night" with it: by night the moon and the sky light leave a cloud as dark as the
            // sky behind it, and the glow is what shows the volumetric ones.
            float styleLight = Mathf.Max(1e-3f, CloudStyle.CloudLightScale);
            _material.SetVector(IdSunDir, clouds.GetVector(IdSunDir));
            _material.SetVector(IdSunColor, clouds.GetVector(IdSunColor) / styleLight);
            _material.SetVector(IdAmbientColor, ambient);
            _material.SetVector(IdNightGlow, clouds.GetVector(IdNightGlow));
            _material.SetVector(IdSunsetEdge, clouds.GetVector(IdSunsetEdge));
            for (int i = 0; i < IdFade.Length; i++)
                _material.SetFloat(IdFade[i], clouds.GetFloat(IdFade[i]));
            // Curved clouds' haze (trial), so the pictures fade exactly as the clouds do.
            _material.SetVector(IdCurveFade, clouds.GetVector(IdCurveFade));

            _renderer.enabled = true;
        }

        /// <summary>
        /// The motion's clock: how far the wind has carried the distant clouds this session, from the
        /// volumetric clouds' drift (sim time: it stops with the game) times <see cref="Speed"/>. A jump of
        /// the drift (a save read, a reset) is not wind: both copies start again from where they are.
        /// </summary>
        private void AdvanceFlow()
        {
            double x = CloudWind.X * Speed, z = CloudWind.Z * Speed;
            if (_haveLast)
            {
                double dx = x - _lastX, dz = z - _lastZ;
                double step = Math.Sqrt(dx * dx + dz * dz);
                if (step < FlowJump)
                {
                    _travel += step;
                    if (step > 1e-3)
                    {
                        _windX = dx / step;
                        _windZ = dz / step;
                    }
                }
                else
                {
                    for (int c = 0; c < 2; c++)
                    {
                        _anchorX[c] += dx;
                        _anchorZ[c] += dz;
                    }
                }
            }
            _lastX = x;
            _lastZ = z;
            _haveLast = true;
        }

        /// <summary>
        /// Every frame, drawn or not (rows at 0, a Dynamic spell without distant clouds): which life each copy holds,
        /// how present it is, and where its carry starts -- so a layer that comes back finds the copies where the
        /// travel has taken them (the first build did this only while drawing: back on after a while, a copy
        /// showed kilometres of leftover carry; harness 2026-11-25, 9-46 km).
        /// </summary>
        private void AdvanceLives()
        {
            int seed = SkyPattern.FieldSeed;
            for (int c = 0; c < 2; c++)
            {
                long life;
                float presence;
                SkyLayerMix.Presence(_travel / Lifetime, c, out life, out presence);
                if (life != _life[c])
                {
                    // A new life: turned another way, its own picture per kind. This city's own turn (its pattern
                    // seed: cities don't share one sky, and "Reset cloud pattern" turns it too), then the golden
                    // angle per life, so no two lives in a row look alike.
                    _life[c] = life;
                    for (int k = 0; k < SkyLayerMix.Kinds; k++)
                        _picture[c][k] = SkyLayerMix.PictureFor(k, life, seed);
                    Anchor(c);
                }

                // Carried from where it first shows: till then (presence 0) the anchor follows.
                if (presence <= 0f)
                    Anchor(c);
                _presence[c] = presence;
            }
        }

        /// <summary>
        /// Copy <paramref name="c"/> for the shader: xy = how far it has been carried in this life (m,
        /// small: invariant 13), zw = cos, sin of its turn. Also fills its pictures (_weights[c]) and cuts
        /// (_cut[c]) per kind; <paramref name="show"/> false = nothing of it to draw.
        /// </summary>
        private Vector4 Copy(int c, out bool show)
        {
            long life = _life[c];
            float presence = _presence[c];
            int seed = SkyPattern.FieldSeed;

            show = false;
            Vector4[] weights = _weights[c];
            for (int k = 0; k < SkyLayerMix.Kinds; k++)
            {
                int p = _picture[c][k];
                float share = _amounts[k] * presence;
                bool on = share > 0f && _hist[p] != null;
                float weight = 1f;
                if (!on)
                {
                    _cut[c][k] = 1f;
                }
                else if (!CoverCut)
                {
                    // THE SLIDER FADES THE ONE PICTURE (his look in game, 2026-11-25: the cut "looks awful,
                    // especially when the slider is at a low value" -- flat white cut-outs with hard edges). The
                    // whole picture (cut -CutSoft: the shader's mask is 1 for any read >= 0) at the share's
                    // opacity: the same mean cloud as the cut at every share, the same shape at every amount.
                    _cut[c][k] = -SkyLayerMix.CutSoft;
                    weight = share;
                }
                else if (share != _cutShare[c][k] || p != _cutPicture[c][k])
                {
                    // Solved again only when the share or the picture moved (a slider, a swap, Dynamic's waves).
                    _cut[c][k] = SkyLayerMix.SolveCut(_hist[p], share);
                    _cutShare[c][k] = share;
                    _cutPicture[c][k] = p;
                }
                show |= on;

                Vector4 a = Vector4.zero, b = Vector4.zero;
                if (p < 4)
                    a[p] = weight;
                else
                    b[p - 4] = weight;
                weights[2 * k] = a;
                weights[2 * k + 1] = b;
            }

            uint hash = unchecked((uint)seed * 2654435761u);
            double turn = hash / 4294967296.0 * 2.0 * Math.PI + life * 2.399963229728653;
            return new Vector4((float)(_lastX - _anchorX[c]), (float)(_lastZ - _anchorZ[c]),
                               (float)Math.Cos(turn), (float)Math.Sin(turn));
        }

        /// <summary>
        /// Copy <paramref name="c"/> starts its carry here: half its span (SkyLayerMix.CarrySpan) downwind of where
        /// the clouds are now, so it shows carried -half .. +half of the span, never far from the picture as
        /// photographed (the flat layer stretches a copy carried far). Downwind = the weather's wind
        /// (CloudWeather.WindDirection, known while paused too); the last motion only where that has none. A wind
        /// that turns during a life carries the copy off that line: it can end a little further out (the game's own
        /// wind swings round every few minutes; harness, an instant 90 deg turn: 5.2 km at most, against 3.45).
        /// </summary>
        private void Anchor(int c)
        {
            double half = 0.5 * SkyLayerMix.CarrySpan * Lifetime;
            double dx = _windX, dz = _windZ;
            Vector3 wind = CloudWeather.WindDirection;
            double length = Math.Sqrt((double)wind.x * wind.x + (double)wind.z * wind.z);
            if (length > 1e-4)
            {
                dx = wind.x / length;
                dz = wind.z / length;
            }
            _anchorX[c] = _lastX + dx * half;
            _anchorZ[c] = _lastZ + dz * half;
        }

        /// <summary>The rows' state: on the log when the layer comes on or goes off, other changes on the detailed log.</summary>
        private void LogState(bool on)
        {
            string text;
            if (!on)
            {
                text = "off";
            }
            else
            {
                // The pictures of the copy showing most (its cut changes all through a swap: not said).
                int lead = _presence[1] > _presence[0] ? 1 : 0;
                var line = new System.Text.StringBuilder();
                for (int k = 0; k < SkyLayerMix.Kinds; k++)
                {
                    if (k > 0)
                        line.Append("; ");
                    line.Append(SkyLayerMix.KindNames[k]).Append(' ').Append((_amounts[k] * 100f).ToString("F0")).Append("% (")
                        .Append(_amounts[k] > 0f ? SkyLayerMix.Pictures[_picture[lead][k]] : "off").Append(')');
                }
                line.Append("; life ").Append(_life[lead]);
                text = line.ToString();
            }

            // The dynamic mix moves on its own: a line each time a percent changes would be a stream, so
            // only its switching on or off is always said.
            bool dynamic = Dynamic;
            if (dynamic)
                text = "dynamic, " + text;

            if (text == _logged)
                return;

            if (_logged == null || on != _loggedOn || dynamic != _loggedDynamic)
                Log.Msg("sky layer: " + text);
            else if (Log.Detailed)
                Log.Detail("sky layer: " + text);

            _logged = text;
            _loggedOn = on;
            _loggedDynamic = dynamic;
        }

        /// <summary>
        /// The eight pictures, decoded (LoadImage, probed in the 5.6 editor 2026-10-12: a JPEG comes out
        /// RGB24, its rows bottom up -- row 0 the horizon) and packed four to a linear, mipmapped RGBA
        /// texture. Once per session; a failure leaves the layer off and says why.
        /// </summary>
        private void Load()
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                Color32[] a = null, b = null;
                int width = 0, height = 0;
                for (int p = 0; p < SkyLayerMix.Pictures.Length; p++)
                {
                    string name = SkyLayerMix.Pictures[p];
                    Color32[] pixels;
                    int w, h;
                    string error = Decode(name, out pixels, out w, out h);
                    if (error != null)
                    {
                        Fail("picture '" + name + "' " + error);
                        return;
                    }

                    if (a == null)
                    {
                        width = w;
                        height = h;
                        a = new Color32[w * h];
                        b = new Color32[w * h];
                    }
                    else if (w != width || h != height)
                    {
                        Fail("picture '" + name + "' is " + w + "x" + h + ", the others " + width + "x" + height);
                        return;
                    }

                    Color32[] target = p < 4 ? a : b;
                    int channel = p % 4;
                    var grey = new float[pixels.Length];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        byte v = pixels[i].r;
                        grey[i] = v / 255f;
                        switch (channel)
                        {
                            case 0: target[i].r = v; break;
                            case 1: target[i].g = v; break;
                            case 2: target[i].b = v; break;
                            default: target[i].a = v; break;
                        }
                    }

                    // For the cut (SkyLayerMix.SolveCut); its whole-picture mean is checked against the baked one
                    // the old rows' values were converted with (SkyLayerMix.PictureMean).
                    var hist = new float[SkyLayerMix.Bins];
                    SkyLayerMix.Histogram(grey, w, h, hist);
                    _hist[p] = hist;
                    float mean = SkyLayerMix.MeanAt(hist, -SkyLayerMix.CutSoft);
                    if (Math.Abs(mean - SkyLayerMix.PictureMean[p]) > 0.01f)
                        Log.Msg("sky layer: picture '" + name + "' has mean " + mean.ToString("F4") + " where " +
                                SkyLayerMix.PictureMean[p].ToString("F4") + " was measured (old rows were converted with that)");
                }

                _picturesA = Make(a, width, height, "VolumetricCloudsSkyPicturesA");
                _picturesB = Make(b, width, height, "VolumetricCloudsSkyPicturesB");
                float megabytes = 2f * width * height * 4f * (4f / 3f) / (1024f * 1024f);
                Log.Msg("sky layer: " + SkyLayerMix.Pictures.Length + " pictures of " + width + "x" + height +
                        " decoded in " + watch.ElapsedMilliseconds + " ms, two textures, " + megabytes.ToString("F1") +
                        " MB of graphics memory with mips; kept for the session");
            }
            catch (Exception e)
            {
                Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        private void Fail(string why)
        {
            _loadFailed = true;
            Log.Warn("sky layer: cannot load its pictures (" + why + "); it stays off this session.");
        }

        private static string Decode(string name, out Color32[] pixels, out int width, out int height)
        {
            pixels = null;
            width = height = 0;
            string resource = "SkyLayer." + name + ".jpg";
            byte[] bytes;
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (stream == null)
                    return "is not embedded (" + resource + ")";
                bytes = new byte[stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }
            }

            Texture2D decoded = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                if (!decoded.LoadImage(bytes))
                    return "could not be decoded";
                width = decoded.width;
                height = decoded.height;
                pixels = decoded.GetPixels32();
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(decoded);
            }
        }

        private static Texture2D Make(Color32[] pixels, int width, int height, string name)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, true, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
            };
            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>For the frame log line: "off" or the four amounts.</summary>
        public string Describe()
        {
            if (!_renderer.enabled)
                return "off";
            return (Dynamic ? "dynamic " : "") + (_amounts[0] * 100f).ToString("F0") + "/" + (_amounts[1] * 100f).ToString("F0") + "/" +
                   (_amounts[2] * 100f).ToString("F0") + "/" + (_amounts[3] * 100f).ToString("F0") + "%" +
                   " behind=" + (_behind * 100f).ToString("F0") + "%";
        }

        public void Dispose()
        {
            if (_holder != null)
                UnityEngine.Object.Destroy(_holder);
            if (_material != null)
                UnityEngine.Object.Destroy(_material);
            if (_picturesA != null)
                UnityEngine.Object.Destroy(_picturesA);
            if (_picturesB != null)
                UnityEngine.Object.Destroy(_picturesB);
        }
    }
}
