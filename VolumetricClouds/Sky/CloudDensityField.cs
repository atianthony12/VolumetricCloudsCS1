using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// A tiling cloud density field, shared by the shadow cookie and the visible clouds so
    /// the two describe the same weather. Generated once; changing coverage only
    /// re-thresholds the cached field, which is cheap enough to do on a slider drag.
    /// </summary>
    public class CloudDensityField
    {
        /// <summary>Texels per side of the map at <see cref="Scale"/> 1: the map as it has always been.</summary>
        public const int BaseResolution = 384;

        /// <summary>
        /// LARGER WEATHER PATTERN (1.5.0, off by default): the map this many times wider, in texels
        /// AND in metres, so it repeats this many times further apart with its clouds the same size
        /// (a 4x repeat: 54 km at the default pattern size). Asked for by a player who zooms out
        /// with ACME, which goes 44 km out (about 70 km of ground across the screen): the default
        /// map came back about five times side by side. The noise's periods grow with it, so every
        /// feature keeps its size in metres; only where they lie is new, so a city's seed draws a
        /// different sky with the switch on (with it off, its own sky, bit for bit).
        /// </summary>
        public const int LargerScale = 4;

        private const int ValueOctaves = 5;
        private const int WorleyOctaves = 3;
        private const int BasePeriod = 4;
        private const int HistogramBins = 256;
        private const int ThresholdSteps = 32;

        // Not readonly: "Reset cloud pattern" swaps in a field generated on a worker thread
        // (Adopt). The simulation thread reads the density and the table (SampleCloud), so they
        // are replaced by reference, never filled in place.
        private float[] _density;
        private Color32[] _pixels;
        private int[] _histogram = new int[HistogramBins];
        private float[] _thresholdTable = new float[ThresholdSteps + 1];
        private int _scale;

        private Texture2D _texture;
        private Texture2D _densityTexture;
        private Texture2D _scoreTexture;

        /// <summary>
        /// Builds the field for a seed. Pure managed maths and arrays, no Unity objects (the
        /// textures are made on first use), so it may run on a worker thread. A seed gives the
        /// same field every time, which is how a save brings its sky back: changing anything in
        /// <see cref="Generate"/> or <see cref="BaseResolution"/> changes every saved sky.
        /// </summary>
        public CloudDensityField(int seed) : this(seed, 1)
        {
        }

        /// <summary>
        /// The same at <paramref name="scale"/> x the size (1, or <see cref="LargerScale"/> for
        /// Larger weather pattern). Scale 1 is the field as it has always been built, on one thread.
        /// </summary>
        public CloudDensityField(int seed, int scale)
        {
            Seed = seed;
            _scale = Mathf.Max(1, scale);
            int n = Resolution;
            _density = new float[n * n];
            _pixels = new Color32[n * n];

            var watch = System.Diagnostics.Stopwatch.StartNew();
            Generate(seed);
            BuildMs = (float)watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>How many times the usual size this map is: 1, or <see cref="LargerScale"/>.</summary>
        /// <summary>The seed this map was made from (the lifecycle's phase salts come from it).</summary>
        public int Seed { get; private set; }

        public int Scale
        {
            get { return _scale; }
        }

        /// <summary>Texels per side of this map.</summary>
        public int Resolution
        {
            get { return BaseResolution * _scale; }
        }

        /// <summary>Milliseconds the pattern took to build, and on how many threads (for the log).</summary>
        public float BuildMs { get; private set; }
        public int BuildThreads { get; private set; }

        /// <summary>
        /// Metres per repeat of THIS map for a "Weather pattern size" of <paramref name="patternSize"/>
        /// (the slider, at least 500 m): <see cref="Scale"/> times it.
        /// </summary>
        public float TileFor(float patternSize)
        {
            return Mathf.Max(500f, patternSize) * _scale;
        }

        /// <summary>
        /// The map's repeat as drawn, in metres. Everything that looks this field up -- the shaders'
        /// _WeatherTile, the rain mask's C# twin, lightning, the rainbow, the fallback cookie --
        /// takes it from here, never from the slider: while a larger map is being built the slider's
        /// switch already says one size and the map still has the other.
        /// </summary>
        public float Tile
        {
            get { return TileFor(Settings.WeatherTileSize != null ? Settings.WeatherTileSize.value : Settings.Defaults.WeatherTileSize); }
        }

        /// <summary>The map for the log: its size, its repeat, how long it took.</summary>
        public string Describe()
        {
            return Resolution + "^2, repeating every " + (Tile / 1000f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " km" +
                   (_scale > 1 ? " (Larger weather pattern: " + _scale + "x the usual, the clouds the same size)" : "") +
                   ", built in " + BuildMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms" +
                   (BuildThreads > 1 ? " on " + BuildThreads + " threads" : "");
        }

        /// <summary>
        /// Goes up by one each time the pattern is replaced, so what was built from the old one
        /// (the fallback cookie) knows to rebuild.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// Takes over another field's pattern ("Reset cloud pattern", or Larger weather pattern
        /// switched). Main thread. Everything holding a reference to THIS field -- the materials,
        /// the rain snapshot, lightning -- sees the new pattern without being told; the textures are
        /// rewritten in place, or, for a map of another size, made again at their next use (every
        /// material is given them again each frame, the sun its cookie too).
        /// </summary>
        public void Adopt(CloudDensityField other)
        {
            bool resized = other._scale != _scale;

            _scoreCache = other._scoreCache;
            _density = other._density;
            _histogram = other._histogram;
            _thresholdTable = other._thresholdTable;
            _scale = other._scale;
            Seed = other.Seed;
            _drawnMean = float.NaN;
            lock (_lifeLock)
            {
                _lifeGeneration++;
                _lifeSolves.Clear();
                _lifePending.Clear();
                _lifeSource = null;
            }
            BuildMs = other.BuildMs;
            BuildThreads = other.BuildThreads;

            // The drawn tables are solved from the density: rebuild them on next use (a null
            // histogram forgets every table's softness too).
            _drawnHistogram = null;

            if (resized)
            {
                _pixels = new Color32[_density.Length];
                DestroyTexture(ref _texture);
                DestroyTexture(ref _densityTexture);
                DestroyTexture(ref _scoreTexture);
                DestroyTexture(ref _emptySkyTexture);
            }
            else
            {
                if (_densityTexture != null)
                    UploadDensity();
                if (_scoreTexture != null)
                    UploadScores();
            }

            Version++;
        }

        private static void DestroyTexture(ref Texture2D texture)
        {
            if (texture != null)
                UnityEngine.Object.Destroy(texture);
            texture = null;
        }

        /// <summary>The cookie texture. Alpha carries the value a directional light samples.</summary>
        public Texture2D Texture
        {
            get
            {
                if (_texture == null)
                {
                    _texture = new Texture2D(Resolution, Resolution, TextureFormat.ARGB32, false)
                    {
                        name = "VolumetricCloudsCookie",
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Bilinear,
                        anisoLevel = 0,
                    };
                }

                return _texture;
            }
        }

        /// <summary>Width of the soft edge band, in density units. The shader needs the same value.</summary>
        public const float EdgeSoftness = Softness;

        /// <summary>
        /// The raw, unthresholded field as a texture. The raymarch shader thresholds it
        /// itself from <see cref="GetThreshold"/>, so a coverage change is one uniform
        /// rather than a texture rewrite -- and it is the same data the cookie is cut from,
        /// so the clouds and their shadows share one weather pattern.
        /// </summary>
        public Texture2D DensityTexture
        {
            get
            {
                if (_densityTexture == null)
                {
                    _densityTexture = new Texture2D(Resolution, Resolution, TextureFormat.ARGB32, false)
                    {
                        name = "VolumetricCloudsWeather",
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Bilinear,
                        anisoLevel = 0,
                    };

                    UploadDensity();
                }

                return _densityTexture;
            }
        }

        private void UploadDensity()
        {
            Color32[] pixels = new Color32[_density.Length];
            for (int i = 0; i < _density.Length; i++)
            {
                byte v = (byte)(Mathf.Clamp01(_density[i]) * 255f);
                pixels[i] = new Color32(v, v, v, v);
            }

            _densityTexture.SetPixels32(pixels);
            _densityTexture.Apply(false);
        }

        /// <summary>
        /// THE CUMULUS STYLE's view of the field (Sky/CloudStyle): each texel's NORMAL SCORE -- its
        /// rank on the map turned into a standard normal z -- stored as (z + <see cref="ScoreRange"/>)
        /// / (2 x ScoreRange) in every channel of a LINEAR texture (the shader reads alpha).
        /// </summary>
        /// <remarks>
        /// The same share of every city's map lies above any score, which the raw values are not:
        /// one city's map sits lower than another's, and measured from the values, the Cumulus
        /// sky was twice as cloudy in one city as in another at the same slider. A rank alone
        /// would flatten the cores; its normal score spreads the top of the map the way these
        /// maps spread (seed 12345: its top 3% and 1% at 1.51 and 1.87 x its median-to-top-tenth
        /// span; normal scores 1.47 and 1.81). A rank is the same whether the GPU decodes the
        /// values or not, so this needs no measurement. Built on first use, rewritten by Adopt.
        /// </remarks>
        public Texture2D ScoreTexture
        {
            get
            {
                if (_scoreTexture == null)
                {
                    _scoreTexture = new Texture2D(Resolution, Resolution, TextureFormat.ARGB32, false, true)
                    {
                        name = "VolumetricCloudsWeatherScore",
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Bilinear,
                        anisoLevel = 0,
                    };

                    UploadScores();
                }

                return _scoreTexture;
            }
        }

        /// <summary>The normal scores the texture holds run over -ScoreRange .. +ScoreRange.</summary>
        public const float ScoreRange = 3f;

        private Texture2D _emptySkyTexture;
        private byte[] _emptySky;
        private int _emptySkyVersion = -1;
        private bool _emptySkySrgb;

        /// <summary>
        /// SKIP THE EMPTY SKY (1.5.0, Sky.EmptySky): per cell of this map, the most cloud it can hold,
        /// for the cloud march to jump over cells that hold none. LINEAR ARGB32, point-filtered,
        /// repeating like the weather map, 96 x 96. Built on first use, again for a new pattern
        /// (Adopt) or once the GPU's decoding is measured (built before, its bounds are only looser:
        /// decoding lowers a value). Main thread.
        /// </summary>
        public Texture2D EmptySkyTexture
        {
            get
            {
                EnsureEmptySky();
                return _emptySkyTexture;
            }
        }

        /// <summary>Milliseconds the empty-sky map last took to build, for the log.</summary>
        public float EmptySkyBuildMs { get; private set; }

        /// <summary>
        /// The share of the map's cells that hold no cloud at this threshold (and, under Cumulus, this
        /// heap line): what the skip has to jump over (Sky.EmptySky.EmptyShare).
        /// </summary>
        public float EmptyShare(float threshold, bool cumulus, float lineA, float lineB)
        {
            EnsureEmptySky();
            return EmptySky.EmptyShare(_emptySky, threshold, cumulus, lineA, lineB);
        }

        private void EnsureEmptySky()
        {
            if (_emptySkyTexture != null && _emptySkyVersion == Version && _emptySkySrgb == GpuReadsSrgb)
                return;

            var watch = System.Diagnostics.Stopwatch.StartNew();

            // The bytes the weather texture holds (UploadDensity), and the score texture's.
            byte[] stored = new byte[_density.Length];
            for (int i = 0; i < stored.Length; i++)
                stored[i] = (byte)(Mathf.Clamp01(_density[i]) * 255f);

            _emptySky = EmptySky.Build(stored, ScoreBytes(), Resolution, GpuReadsSrgb);

            int size = Resolution / EmptySky.CellTexels;
            if (_emptySkyTexture == null)
            {
                _emptySkyTexture = new Texture2D(size, size, TextureFormat.ARGB32, false, true)
                {
                    name = "VolumetricCloudsEmptySky",
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Point,
                    anisoLevel = 0,
                };
            }

            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color32(_emptySky[i * 4], _emptySky[i * 4 + 1], _emptySky[i * 4 + 2], _emptySky[i * 4 + 3]);

            _emptySkyTexture.SetPixels32(pixels);
            _emptySkyTexture.Apply(false);

            _emptySkyVersion = Version;
            _emptySkySrgb = GpuReadsSrgb;
            EmptySkyBuildMs = (float)watch.Elapsed.TotalMilliseconds;
        }

        private void UploadScores()
        {
            byte[] scores = ScoreBytes();
            Color32[] pixels = new Color32[scores.Length];
            for (int i = 0; i < scores.Length; i++)
            {
                byte v = scores[i];
                pixels[i] = new Color32(v, v, v, v);
            }

            _scoreTexture.SetPixels32(pixels);
            _scoreTexture.Apply(false);
        }

        // The scores of one density array, kept: a larger map's take ~16x as long, and the score
        // texture, the empty-sky map and the Mixed style's bake each ask. One reference, so the
        // bytes and the array they belong to are always read together.
        private sealed class ScoreCache
        {
            public float[] Density;
            public byte[] Bytes;
        }

        private volatile ScoreCache _scoreCache;

        /// <summary>
        /// Every texel's normal score as the texture stores it. Pure managed maths (the offline
        /// preview and tests read it): the rank from the histogram, with the place inside the bin.
        /// Worked out once per pattern; never write into the array it returns.
        /// </summary>
        public byte[] ScoreBytes()
        {
            float[] density = _density;
            int[] histogram = _histogram;

            ScoreCache cache = _scoreCache;
            if (cache != null && ReferenceEquals(cache.Density, density))
                return cache.Bytes;

            byte[] bytes = Scores(density, histogram);

            // Kept only if the pattern did not change while they were worked out (a worker thread
            // can ask while the main thread adopts a new one): density and histogram one pattern's.
            if (ReferenceEquals(density, _density) && ReferenceEquals(histogram, _histogram))
                _scoreCache = new ScoreCache { Density = density, Bytes = bytes };

            return bytes;
        }

        private static byte[] Scores(float[] density, int[] histogram)
        {
            int n = density.Length;

            // How many texels lie in the bins below each bin.
            int[] below = new int[HistogramBins + 1];
            for (int bin = 0; bin < HistogramBins; bin++)
                below[bin + 1] = below[bin] + histogram[bin];

            byte[] bytes = new byte[n];
            float half = 0.5f / n;
            for (int i = 0; i < n; i++)
            {
                float position = Mathf.Clamp01(density[i]) * HistogramBins;
                int bin = Mathf.Clamp((int)position, 0, HistogramBins - 1);
                float inside = Mathf.Clamp01(position - bin);
                float rank = Mathf.Clamp((below[bin] + inside * histogram[bin]) / n, half, 1f - half);

                float z = Mathf.Clamp(NormalQuantile(rank), -ScoreRange, ScoreRange);
                int v = (int)((z + ScoreRange) / (2f * ScoreRange) * 255f + 0.5f);
                bytes[i] = (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
            }

            return bytes;
        }

        /// <summary>
        /// The standard normal quantile (the z below which a share p of a normal distribution lies):
        /// Acklam's rational approximation, relative error under 1.2e-9.
        /// </summary>
        public static float NormalQuantile(float p)
        {
            double q, r;
            const double low = 0.02425, high = 1 - 0.02425;

            if (p <= 0f)
                return float.NegativeInfinity;
            if (p >= 1f)
                return float.PositiveInfinity;

            if (p < low)
            {
                q = System.Math.Sqrt(-2 * System.Math.Log(p));
                return (float)((((((-7.784894002430293e-03 * q - 3.223964580411365e-01) * q - 2.400758277161838e+00) * q - 2.549732539343734e+00) * q + 4.374664141464968e+00) * q + 2.938163982698783e+00)
                               / ((((7.784695709041462e-03 * q + 3.224671290700398e-01) * q + 2.445134137142996e+00) * q + 3.754408661907416e+00) * q + 1));
            }

            if (p > high)
            {
                q = System.Math.Sqrt(-2 * System.Math.Log(1 - p));
                return -(float)((((((-7.784894002430293e-03 * q - 3.223964580411365e-01) * q - 2.400758277161838e+00) * q - 2.549732539343734e+00) * q + 4.374664141464968e+00) * q + 2.938163982698783e+00)
                                / ((((7.784695709041462e-03 * q + 3.224671290700398e-01) * q + 2.445134137142996e+00) * q + 3.754408661907416e+00) * q + 1));
            }

            q = p - 0.5;
            r = q * q;
            return (float)((((((-3.969683028665376e+01 * r + 2.209460984245205e+02) * r - 2.759285104469687e+02) * r + 1.383577518672690e+02) * r - 3.066479806614716e+01) * r + 2.506628277459239e+00) * q
                           / (((((-5.447609879822406e+01 * r + 1.615858368580409e+02) * r - 1.556989798598866e+02) * r + 6.680131188771972e+01) * r - 1.328068155288572e+01) * r + 1));
        }

        /// <summary>The density threshold that yields the requested sky coverage.</summary>
        public float GetThreshold(float coverage)
        {
            // Main thread only (the material's values): the lifecycle's eased cover when it runs and its solves are ready.
            float row = CloudLifecycle.Row;
            if (CloudLifecycle.Active(row))
            {
                float eased;
                if (LifeThreshold(CloudLifecycle.Clock, row, coverage, out eased))
                    return eased;
                System.Threading.Interlocked.Increment(ref LifeFallbacks);
            }
            return Threshold(coverage);
        }

        /// <summary>
        /// Cloud amount at a point in field space, 0..1, for a given coverage: the CPU twin of
        /// the shaders' "saturate((weather - threshold) / softness)". Called from the
        /// simulation thread (CloudRain.LocalRain), so it touches nothing but these arrays.
        /// </summary>
        // ---- the cloud lifecycle's cover (2026-11-25, round 3 (d)) ---------------------------------------------------
        // The threshold of the BLENDED map, re-solved every CloudLifecycle.ResolveSeconds of sim time on a worker, in the
        // drawn-histogram space (as GetThresholdAsDrawn), and eased linearly between two neighbouring solves. The main thread
        // asks for the grid's solves one interval ahead; a solve is stamped with the map's generation and the row, and dropped
        // if the map was adopted or the row changed meanwhile. Row 0 never reaches this code.

        /// <summary>Tests and the bench: solve inline instead of on a worker. Off in the game.</summary>
        public static bool SynchronousSolves = false;

        /// <summary>Diagnostics: how many reads fell back to the plain threshold because their solve pair was not ready.</summary>
        public static long LifeFallbacks;

        private sealed class LifeSolve
        {
            public int[] Hist;
            public long Total;
            public CloudLifecycle.CoverTable Table;

            /// <summary>The blended NORMAL SCORES' histogram (CloudLifecycle.ScoreBins) of the same solve: the heaps' line.</summary>
            public int[] ScoreHist;
            public long ScoreTotal;
        }

        /// <summary>Diagnostics: how many heaps-line reads fell back to the design line (the solve pair was not ready).</summary>
        public static long HeapFallbacks;

        private int[] _plainScoreHist;
        private long _plainScoreTotal;
        private float[] _plainScoreFor;

        private sealed class LifeJob
        {
            public int Index;
            public float Row;
            public int Generation;
            public float[] Density;
            public int Side;
            public int Seed;
            public int Scale;

            /// <summary>The map's plain (stored-space) threshold table, the clouds' cover the lifecycle must keep.</summary>
            public float[] PlainTable;
        }

        private readonly object _lifeLock = new object();
        private readonly System.Collections.Generic.Dictionary<int, LifeSolve> _lifeSolves = new System.Collections.Generic.Dictionary<int, LifeSolve>();
        private readonly System.Collections.Generic.HashSet<int> _lifePending = new System.Collections.Generic.HashSet<int>();
        private int _lifeGeneration;

        /// <summary>
        /// What every solve of one map needs and the clock never changes: the drawn values (as the GPU reads them), their mean and
        /// the score bytes. Made ONCE per map on the first worker that needs it (2026-11-25 freeze fix: each solve used to make
        /// them again, ~12 MB a solve on a 1536^2 map, which the game's garbage collector pays for).
        /// </summary>
        private sealed class LifeSource
        {
            public float[] For;
            public float[] Drawn;
            public float Mean;
            public byte[] Scores;

            /// <summary>The plain drawn values' histogram (CoverTable.Bins): the share the plain sky really covers.</summary>
            public int[] DrawnHist;
        }

        private LifeSource _lifeSource;
        private readonly object _lifeSourceLock = new object();

        /// <summary>Main thread only: StepLife's job list (kept, so a frame allocates nothing).</summary>
        private readonly System.Collections.Generic.List<LifeJob> _lifeJobs = new System.Collections.Generic.List<LifeJob>(2);
        private float _lifeNextLog;

        /// <summary>Diagnostics (detailed log): solves made, and the last one's time on its worker.</summary>
        public static long LifeSolvesMade;
        public static double LifeLastSolveMs;

        /// <summary>
        /// Main thread, once a frame, before the material is set (CloudVolume.LateUpdate): asks a worker for the solves of the
        /// clock's grid interval and the next one. NEVER solves on the main thread (2026-11-25 freeze fix: the first pair used to
        /// be solved here, and every slider step threw the pairs away -- seconds a step on his map); until a pair is ready the
        /// readers take the plain threshold (LifeFallbacks), for a second or so after the row is first turned on. The grid is in
        /// clock units, so moving the slider keeps every solve. Nothing runs at row 0.
        /// </summary>
        public void StepLife(double clock, float row)
        {
            if (!CloudLifecycle.Active(row))
                return;

            int n = CloudLifecycle.ResolveCount(row);
            int k = CloudLifecycle.ResolveIndex(clock, row);
            int k1 = (k + 1) % n;
            // One step AHEAD too (2026-11-25 fix: "glitching"): asking for the next solve only when the clock reached its step
            // left every step's first ~0.2 s on the plain cover, a jump in the sky every eighth of a cycle.
            int k2 = (k + 2) % n;
            float[] density = _density;
            int side = SideOf(density);

            if (Log.Detailed && Time.realtimeSinceStartup >= _lifeNextLog)
            {
                _lifeNextLog = Time.realtimeSinceStartup + 10f;
                Log.Detail("clouds form and fade: clock " + clock.ToString("0.000") + " cycles, row " + (row * 100f).ToString("0") +
                           "%, grid step " + k + "/" + n + "; cover solves made " + LifeSolvesMade + " (last " + LifeLastSolveMs.ToString("0") +
                           " ms on a worker); reads on the plain cover while a solve was not ready: " + LifeFallbacks + ", heap line " + HeapFallbacks);
            }

            System.Collections.Generic.List<LifeJob> jobs = _lifeJobs;
            jobs.Clear();
            lock (_lifeLock)
            {
                RequestLife(k, row, density, side, jobs);
                RequestLife(k1, row, density, side, jobs);
                RequestLife(k2, row, density, side, jobs);

                // Keep only the pairs near the clock (a slider pass or a long session would otherwise grow the table).
                if (_lifeSolves.Count > 8)
                {
                    System.Collections.Generic.List<int> old = new System.Collections.Generic.List<int>();
                    foreach (int key in _lifeSolves.Keys)
                        if (key != k && key != k1 && key != k2)
                            old.Add(key);
                    for (int i = 0; i < old.Count; i++)
                        _lifeSolves.Remove(old[i]);
                }
            }

            for (int i = 0; i < jobs.Count; i++)
            {
                if (SynchronousSolves)
                    SolveLife(jobs[i]);
                else
                    System.Threading.ThreadPool.QueueUserWorkItem(SolveLifeJob, jobs[i]);
            }
        }

        private void RequestLife(int index, float row, float[] density, int side, System.Collections.Generic.List<LifeJob> jobs)
        {
            if (_lifeSolves.ContainsKey(index) || _lifePending.Contains(index))
                return;
            _lifePending.Add(index);
            jobs.Add(new LifeJob { Index = index, Row = row, Generation = _lifeGeneration, Density = density, Side = side, Seed = Seed, Scale = _scale,
                                   PlainTable = _thresholdTable });
        }

        private void SolveLifeJob(object state)
        {
            LifeJob job = (LifeJob)state;
            try
            {
                SolveLife(job);
            }
            catch (System.Exception e)
            {
                lock (_lifeLock)
                    _lifePending.Remove(job.Index);
                Log.Msg("clouds form and fade: a cover solve failed (" + e.GetType().Name + ": " + e.Message + "); the plain cover is used");
            }
        }

        /// <summary>The map's lifecycle source, made once (on whichever worker asks first).</summary>
        private LifeSource SourceFor(float[] density)
        {
            lock (_lifeSourceLock)
            {
                LifeSource s = _lifeSource;
                if (s != null && ReferenceEquals(s.For, density))
                    return s;

                float[] drawn = new float[density.Length];
                int[] drawnHist = new int[CloudLifecycle.CoverTable.Bins];
                double sum = 0.0;
                for (int i = 0; i < drawn.Length; i++)
                {
                    drawn[i] = AsTheGpuReadsIt(density[i]);
                    sum += drawn[i];
                    drawnHist[Mathf.Clamp((int)(drawn[i] * CloudLifecycle.CoverTable.Bins), 0, CloudLifecycle.CoverTable.Bins - 1)]++;
                }
                s = new LifeSource { For = density, Drawn = drawn, Mean = (float)(sum / drawn.Length), Scores = ScoreBytesOf(density), DrawnHist = drawnHist };
                _lifeSource = s;
                return s;
            }
        }

        private void SolveLife(LifeJob job)
        {
            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

            // The drawn values, their mean and the score bytes of the array this job was asked for (made once per map).
            LifeSource source = SourceFor(job.Density);

            int saltX, saltY;
            CloudLifecycle.SaltsFor(job.Seed, out saltX, out saltY);
            int cells = CloudLifecycle.CellsFor(CloudLifecycle.CellsPer13km, job.Scale);
            double clock = CloudLifecycle.ResolveClock(job.Index, job.Row);
            int stride = CloudLifecycle.StrideFor(job.Side);
            int[] hist = new int[256];
            long total = CloudLifecycle.BlendHistogram(source.Drawn, job.Side, source.Mean, clock, job.Row, cells, saltX, saltY, hist, stride);

            // The heaps' line: the same blend over the normal scores the texture holds.
            int[] scoreHist = new int[CloudLifecycle.ScoreBins];
            long scoreTotal = CloudLifecycle.BlendScoreHistogram(source.Scores, job.Side, clock, job.Row, cells, saltX, saltY, scoreHist, stride);

            LifeSolve solve = new LifeSolve
            {
                // The clouds' table keeps the PLAIN sky's real cover (the plain threshold is in stored space, the reads are decoded).
                Hist = hist, Total = total,
                Table = CloudLifecycle.CoverTable.MatchingPlain(hist, total, source.DrawnHist, source.Drawn.Length, job.PlainTable),
                ScoreHist = scoreHist, ScoreTotal = scoreTotal,
            };
            lock (_lifeLock)
            {
                _lifePending.Remove(job.Index);
                if (job.Generation != _lifeGeneration)
                    return;
                _lifeSolves[job.Index] = solve;
            }

            watch.Stop();
            LifeLastSolveMs = watch.Elapsed.TotalMilliseconds;
            long made = System.Threading.Interlocked.Increment(ref LifeSolvesMade);
            if (made == 1)
                Log.Msg("clouds form and fade: first cover solve ready in " + LifeLastSolveMs.ToString("0") + " ms on a worker (map " +
                        job.Side + "^2 sampled every " + stride + " texels); later ones every eighth of a cycle");
        }

        /// <summary>
        /// The eased threshold of the blended map for a coverage (softness <see cref="Softness"/>), at a clock and row. False when
        /// the pair of solves around the clock is not ready (the caller then uses the plain threshold). A flag, not a sentinel
        /// (2026-11-25 fix): a threshold is NEGATIVE at high cover (down to -softness), and "-1 = not ready" with callers testing
        /// ">= 0" sent every high-cover read back to the plain threshold.
        /// </summary>
        private bool LifeThreshold(double clock, float row, float coverage, out float threshold)
        {
            threshold = 0f;
            int n = CloudLifecycle.ResolveCount(row);
            int k = CloudLifecycle.ResolveIndex(clock, row);
            int k1 = (k + 1) % n;
            float f = CloudLifecycle.ResolveFraction(clock, row);
            lock (_lifeLock)
            {
                LifeSolve a, b;
                if (!_lifeSolves.TryGetValue(k, out a) || !_lifeSolves.TryGetValue(k1, out b))
                    return false;
                float ta = a.Table.GetThreshold(coverage), tb = b.Table.GetThreshold(coverage);
                threshold = ta + (tb - ta) * f;
                return true;
            }
        }

        /// <summary>The same for any softness (GetThresholdAsDrawn's callers: the fog, the halos, the funnels' fog). False when not ready.</summary>
        private bool LifeThresholdAs(double clock, float row, float coverage, float softness, out float threshold)
        {
            threshold = 0f;
            int n = CloudLifecycle.ResolveCount(row);
            int k = CloudLifecycle.ResolveIndex(clock, row);
            int k1 = (k + 1) % n;
            float f = CloudLifecycle.ResolveFraction(clock, row);
            LifeSolve a, b;
            lock (_lifeLock)
            {
                if (!_lifeSolves.TryGetValue(k, out a) || !_lifeSolves.TryGetValue(k1, out b))
                    return false;
            }
            float ta = CloudLifecycle.CoverTable.Solve(a.Hist, a.Total, coverage, softness);
            float tb = CloudLifecycle.CoverTable.Solve(b.Hist, b.Total, coverage, softness);
            threshold = ta + (tb - ta) * f;
            return true;
        }

        /// <summary>The score bytes of a density array, worked out on any thread (the same rule as ScoreBytes: the bins of
        /// <c>(int)(value x HistogramBins)</c> clamped, then Scores). Never reads the field's current pattern.</summary>
        private static byte[] ScoreBytesOf(float[] density)
        {
            int[] histogram = new int[HistogramBins];
            for (int i = 0; i < density.Length; i++)
            {
                int bin = Mathf.Clamp((int)(density[i] * HistogramBins), 0, HistogramBins - 1);
                histogram[bin]++;
            }
            return Scores(density, histogram);
        }

        /// <summary>The plain map's score histogram (main thread; cached per pattern): the share of the plain map above any score.</summary>
        private int[] PlainScoreHist(out long total)
        {
            float[] density = _density;
            if (_plainScoreHist == null || !ReferenceEquals(_plainScoreFor, density))
            {
                byte[] bytes = ScoreBytes();
                int[] hist = new int[CloudLifecycle.ScoreBins];
                for (int i = 0; i < bytes.Length; i++)
                    hist[CloudLifecycle.ScoreBin(CloudLifecycle.ScoreOfByte(bytes[i]))]++;
                _plainScoreHist = hist;
                _plainScoreTotal = bytes.Length;
                _plainScoreFor = density;
            }

            total = _plainScoreTotal;
            return _plainScoreHist;
        }

        /// <summary>
        /// THE HEAPS' LINE UNDER THE LIFECYCLE (2026-11-25, task 1). <paramref name="designEdge"/> = the line's edge the plain map
        /// uses (CloudStyle.HeapEdge(HeapCoverage(cover))); the heaps are drawn where edge + perScore x score > 0, so their share
        /// is the plain map's share above z = -edge / perScore. The blended scores have another share above that line, so the
        /// line moves: this returns the edge whose share of the BLENDED scores matches the plain map's, solved on each grid step
        /// and eased like the value threshold. Off (row 0) or not ready: the design edge, counted in <see cref="HeapFallbacks"/>.
        /// Main thread only.
        /// </summary>
        public float HeapEdgeEased(float designEdge)
        {
            float row = CloudLifecycle.Row;
            if (!CloudLifecycle.Active(row))
                return designEdge;

            float perScore = CloudStyle.CoverageSlope / CloudStyle.TopTenthScore;
            long plainTotal;
            int[] plain = PlainScoreHist(out plainTotal);
            float share = CloudLifecycle.ShareAbove(plain, plainTotal, -designEdge / perScore);

            double clock = CloudLifecycle.Clock;
            int n = CloudLifecycle.ResolveCount(row);
            int k = CloudLifecycle.ResolveIndex(clock, row);
            int k1 = (k + 1) % n;
            float f = CloudLifecycle.ResolveFraction(clock, row);
            LifeSolve a, b;
            lock (_lifeLock)
            {
                if (!_lifeSolves.TryGetValue(k, out a) || !_lifeSolves.TryGetValue(k1, out b))
                {
                    System.Threading.Interlocked.Increment(ref HeapFallbacks);
                    return designEdge;
                }
            }

            float za = CloudLifecycle.ZAtShare(a.ScoreHist, a.ScoreTotal, share);
            float zb = CloudLifecycle.ZAtShare(b.ScoreHist, b.ScoreTotal, share);
            float z = za + (zb - za) * f;
            return -z * perScore;
        }

        public float SampleCloud(float u, float v, float coverage)
        {
            // The side from the array itself: a larger map can be adopted on the main thread between
            // two reads here, and the array and its size must be one pattern's.
            float[] density = _density;
            int n = SideOf(density);
            float value;
            float row;
            double clock;
            CloudLifecycle.Read(out row, out clock);
            if (CloudLifecycle.Active(row))
            {
                // Clouds form and fade (2026-11-25): the two offset reads of the lifecycle, as the shader makes them.
                int saltX, saltY;
                CloudLifecycle.SaltsFor(Seed, out saltX, out saltY);
                int cells = CloudLifecycle.CellsFor(CloudLifecycle.CellsPer13km, _scale);
                float snap = CloudLifecycle.SnapFor(n);
                float clockFrac = CloudLifecycle.ClockFraction(clock);
                int cycle = CloudLifecycle.ClockCycle(clock);
                CloudLifecycle.Look look = CloudLifecycle.LookAt(u, v, clockFrac, cycle, cells, saltX, saltY, snap, true);
                float a0 = DrawnAt(density, n, u + look.U0, v + look.V0);
                float a1 = DrawnAt(density, n, u + look.U1, v + look.V1);
                value = CloudLifecycle.Blend(a0, a1, DrawnMean(density, n), look);
            }
            else
            {
                value = DrawnAt(density, n, u, v);
            }
            float threshold = Threshold(coverage);
            if (CloudLifecycle.Active(row))
            {
                float eased;
                if (LifeThreshold(clock, row, coverage, out eased))
                    threshold = eased;
                else
                    System.Threading.Interlocked.Increment(ref LifeFallbacks);
            }
            return Mathf.Clamp01((value - threshold) / Softness);
        }

        private float _drawnMean = float.NaN;

        /// <summary>The drawn map's mean (the blend's centre), computed once per map and reset on Adopt.</summary>
        private float DrawnMean(float[] density, int n)
        {
            if (float.IsNaN(_drawnMean))
            {
                double sum = 0.0;
                for (int i = 0; i < density.Length; i++)
                    sum += AsTheGpuReadsIt(density[i]);
                _drawnMean = (float)(sum / density.Length);
            }
            return _drawnMean;
        }

        /// <summary>The drawn value at a point of the drifting frame: the nearest texel, wrapped (as this twin always has).</summary>
        private float DrawnAt(float[] density, int n, float u, float v)
        {
            int x = Wrap(Mathf.FloorToInt(u * n), n);
            int y = Wrap(Mathf.FloorToInt(v * n), n);
            return AsTheGpuReadsIt(density[y * n + x]);
        }

        private static int SideOf(float[] density)
        {
            return density.Length == BaseResolution * BaseResolution
                ? BaseResolution
                : (int)(System.Math.Sqrt(density.Length) + 0.5);
        }

        /// <summary>
        /// True if the GPU gamma-decodes <see cref="DensityTexture"/> when a shader samples it.
        /// </summary>
        /// <remarks>
        /// The texture is created without the "linear" flag, the game renders in linear colour
        /// space, and in that combination Unity treats an ARGB32 texture as sRGB: a stored 0.5
        /// reaches the shader as 0.21. The clouds were TUNED like that and look right, so the
        /// texture stays as it is -- but the CPU twin above must then read the field the same
        /// way, or the rain the player sees and the rain the roads and the sound react to are
        /// cut from two different fields. Rather than reason about it, it is measured once:
        /// see <see cref="MeasureGpuDecoding"/>.
        /// </remarks>
        public bool GpuReadsSrgb { get; private set; }

        private bool _decodingMeasured;

        /// <summary>
        /// Blits the density texture to a LINEAR render target and reads it back. The texture
        /// holds the same value in all four channels, and a GPU only ever gamma-decodes RGB,
        /// never alpha -- so if red comes back darker than alpha, shaders see decoded values.
        /// Main thread, once; needs nothing but the texture.
        /// </summary>
        public void MeasureGpuDecoding()
        {
            if (_decodingMeasured)
                return;

            _decodingMeasured = true;

            const int size = 64;
            RenderTexture target = RenderTexture.GetTemporary(size, size, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = null;

            try
            {
                Graphics.Blit(DensityTexture, target);

                readback = new Texture2D(size, size, TextureFormat.ARGB32, false, true);
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);

                // Mid-range texels only: at 0 and 1 the two readings agree anyway.
                Color32[] pixels = readback.GetPixels32();
                double red = 0, alpha = 0, decoded = 0;
                int used = 0;

                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].a < 90 || pixels[i].a > 200)
                        continue;

                    red += pixels[i].r / 255.0;
                    alpha += pixels[i].a / 255.0;
                    decoded += SrgbToLinear(pixels[i].a / 255f);
                    used++;
                }

                if (used == 0)
                {
                    Log.Warn("weather texture readback found no mid-range texels; assuming shaders read it undecoded.");
                    return;
                }

                red /= used;
                alpha /= used;
                decoded /= used;

                GpuReadsSrgb = System.Math.Abs(red - decoded) < System.Math.Abs(red - alpha);

                Log.Msg("weather texture as the GPU reads it: stored " + alpha.ToString("F3") + " comes back as " +
                        red.ToString("F3") + " (undecoded would be " + alpha.ToString("F3") + ", sRGB-decoded " +
                        decoded.ToString("F3") + ") over " + used + " texels -> " +
                        (GpuReadsSrgb
                            ? "DECODED; the CPU twin now decodes too, so rain sound, wet roads and lightning agree with what is drawn"
                            : "not decoded; the CPU twin already matches"));
            }
            catch (System.Exception e)
            {
                Log.Error("Measuring how the GPU reads the weather texture threw.", e);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (readback != null)
                    UnityEngine.Object.Destroy(readback);
            }
        }

        private float AsTheGpuReadsIt(float stored)
        {
            return GpuReadsSrgb ? SrgbToLinear(stored) : stored;
        }

        private float[] _statsFor;
        private bool _statsSrgb;
        private float _readMean, _readSpread;

        /// <summary>
        /// The map's mean and standard deviation AS THE SHADERS READ IT (gamma-decoded where the GPU
        /// decodes): Curved clouds' far patches blend their lookups round the mean so the contrast stays
        /// the map's, and the fronts move it in standard deviations. Main thread; worked out once per
        /// pattern (and again if the decoding measurement changes it).
        /// </summary>
        public void ReadStats(out float mean, out float spread)
        {
            float[] density = _density;
            if (!ReferenceEquals(_statsFor, density) || _statsSrgb != GpuReadsSrgb)
            {
                double sum = 0.0, squares = 0.0;
                for (int i = 0; i < density.Length; i++)
                {
                    double v = AsTheGpuReadsIt(density[i]);
                    sum += v;
                    squares += v * v;
                }

                double m = sum / density.Length;
                _readMean = (float)m;
                _readSpread = (float)System.Math.Sqrt(System.Math.Max(squares / density.Length - m * m, 1e-8));
                _statsFor = density;
                _statsSrgb = GpuReadsSrgb;
            }

            mean = _readMean;
            spread = _readSpread;
        }

        /// <summary>The normal scores' standard deviation as stored: (z + ScoreRange) / (2 ScoreRange), z ~ N(0, 1).</summary>
        public const float ScoreSpread = 1f / (2f * ScoreRange);

        // One solved table per softness asked for, over one shared histogram: the fog (0.1) and
        // the cloud fragments (the clouds' softness) both ask every frame, and a single slot
        // would rebuild the whole histogram on every call.
        private const int DrawnSlots = 2;
        private int[] _drawnHistogram;
        private bool _drawnForSrgb;
        private readonly float[][] _drawnTables = new float[DrawnSlots][];
        private readonly float[] _drawnSoftness = { -1f, -1f };
        private int _drawnNext;

        /// <summary>
        /// The threshold that really covers <paramref name="coverage"/> of the field ON
        /// SCREEN, for a shader that tests "saturate((weather - threshold) / softness)".
        /// </summary>
        /// <remarks>
        /// <see cref="GetThreshold"/> is solved against the stored densities, but the GPU
        /// compares gamma-decoded ones (measured: 0.547 is read as 0.277), so its "40%" covers
        /// far less than 40%. The clouds were tuned against exactly that and must keep it --
        /// and the rain with them, whose "always beneath cloud" guarantee comes from sharing
        /// the clouds' table. Anything NEW that promises the player a share of the map (the
        /// fog, the cloud fragments) asks here instead. Main thread only; rebuilt if the pattern
        /// or the measurement changes, and solved once per softness.
        /// </remarks>
        public float GetThresholdAsDrawn(float coverage, float softness)
        {
            float row = CloudLifecycle.Row;
            if (CloudLifecycle.Active(row))
            {
                float eased;
                if (LifeThresholdAs(CloudLifecycle.Clock, row, coverage, softness, out eased))
                    return eased;
            }

            if (_drawnHistogram == null || _drawnForSrgb != GpuReadsSrgb)
            {
                if (_drawnHistogram == null)
                    _drawnHistogram = new int[HistogramBins];

                System.Array.Clear(_drawnHistogram, 0, _drawnHistogram.Length);
                for (int i = 0; i < _density.Length; i++)
                {
                    int bin = Mathf.Clamp((int)(AsTheGpuReadsIt(_density[i]) * HistogramBins), 0, HistogramBins - 1);
                    _drawnHistogram[bin]++;
                }

                _drawnForSrgb = GpuReadsSrgb;
                for (int slot = 0; slot < DrawnSlots; slot++)
                    _drawnSoftness[slot] = -1f;
            }

            float[] table = null;
            for (int slot = 0; slot < DrawnSlots; slot++)
            {
                if (_drawnTables[slot] != null && Mathf.Approximately(_drawnSoftness[slot], softness))
                {
                    table = _drawnTables[slot];
                    break;
                }
            }

            if (table == null)
            {
                int slot = _drawnNext;
                _drawnNext = (_drawnNext + 1) % DrawnSlots;

                table = _drawnTables[slot] ?? new float[ThresholdSteps + 1];
                for (int step = 0; step <= ThresholdSteps; step++)
                    table[step] = Solve(_drawnHistogram, step / (float)ThresholdSteps, softness);

                _drawnTables[slot] = table;
                _drawnSoftness[slot] = softness;
            }

            float t = Mathf.Clamp01(coverage) * ThresholdSteps;
            int index = Mathf.Clamp((int)t, 0, ThresholdSteps - 1);
            return Mathf.Lerp(table[index], table[index + 1], t - index);
        }

        /// <summary>The bisection of <see cref="SolveThreshold"/>, for any histogram and softness.</summary>
        private float Solve(int[] histogram, float targetMean, float softness)
        {
            if (targetMean <= 0f)
                return 1f + softness;
            if (targetMean >= 1f)
                return -softness;

            float low = -softness;
            float high = 1f + softness;

            for (int i = 0; i < 24; i++)
            {
                float mid = (low + high) * 0.5f;

                float sum = 0f;
                for (int bin = 0; bin < HistogramBins; bin++)
                {
                    if (histogram[bin] != 0)
                        sum += Mathf.Clamp01(((bin + 0.5f) / HistogramBins - mid) / softness) * histogram[bin];
                }

                // Mean falls as the threshold rises.
                if (sum / _density.Length > targetMean)
                    low = mid;
                else
                    high = mid;
            }

            return (low + high) * 0.5f;
        }

        /// <summary>The sRGB transfer function, in plain managed maths: this runs on the simulation thread.</summary>
        private static float SrgbToLinear(float v)
        {
            if (v <= 0.04045f)
                return v / 12.92f;

            return (float)System.Math.Pow((v + 0.055f) / 1.055f, 2.4);
        }

        private const float Softness = 0.18f;

        /// <summary>
        /// Threshold for a given coverage, looked up from a table solved at generation so
        /// that "40%" really means 40% of the sky. A linear sweep leaves the bottom half of
        /// the slider doing nothing, and a plain percentile still undershoots because the
        /// soft edge band loses more than it gains on a skewed distribution.
        /// </summary>
        private float Threshold(float coverage)
        {
            coverage = Mathf.Clamp01(coverage);

            float t = coverage * ThresholdSteps;
            int index = Mathf.Clamp((int)t, 0, ThresholdSteps - 1);
            return Mathf.Lerp(_thresholdTable[index], _thresholdTable[index + 1], t - index);
        }

        /// <summary>
        /// Solves, for each coverage step, the threshold whose resulting mean cloud amount
        /// equals it. Evaluated against the histogram rather than the full field, which
        /// makes each probe 256 operations instead of 147k.
        /// </summary>
        private void BuildThresholdTable()
        {
            for (int i = 0; i < _histogram.Length; i++)
                _histogram[i] = 0;

            for (int i = 0; i < _density.Length; i++)
            {
                int bin = Mathf.Clamp((int)(_density[i] * HistogramBins), 0, HistogramBins - 1);
                _histogram[bin]++;
            }

            for (int step = 0; step <= ThresholdSteps; step++)
                _thresholdTable[step] = SolveThreshold(step / (float)ThresholdSteps);
        }

        private float SolveThreshold(float targetMean)
        {
            if (targetMean <= 0f)
                return 1f + Softness;
            if (targetMean >= 1f)
                return -Softness;

            // Mean falls as the threshold rises, so bracket and bisect.
            float low = -Softness;
            float high = 1f + Softness;

            for (int i = 0; i < 24; i++)
            {
                float mid = (low + high) * 0.5f;
                if (SoftMean(mid) > targetMean)
                    low = mid;
                else
                    high = mid;
            }

            return (low + high) * 0.5f;
        }

        private float SoftMean(float threshold)
        {
            float sum = 0f;

            for (int bin = 0; bin < HistogramBins; bin++)
            {
                if (_histogram[bin] == 0)
                    continue;

                float centre = (bin + 0.5f) / HistogramBins;
                sum += Mathf.Clamp01((centre - threshold) / Softness) * _histogram[bin];
            }

            return sum / _density.Length;
        }

        /// <summary>
        /// Rewrites the fallback cookie for a given coverage: full light in the gaps, and
        /// (1 - depth) under cloud. Same meaning as the marched shadow map, just flat and
        /// unaligned -- it is only used when the shader bundle is unavailable.
        /// </summary>
        public void Apply(float coverage, float depth)
        {
            float threshold = Threshold(coverage);
            depth = Mathf.Clamp01(depth);

            for (int i = 0; i < _density.Length; i++)
            {
                float cloud = Mathf.Clamp01((_density[i] - threshold) / Softness);
                byte v = (byte)((1f - cloud * depth) * 255f);
                _pixels[i] = new Color32(v, v, v, v);
            }

            Texture.SetPixels32(_pixels);
            Texture.Apply(false);
        }

        /// <summary>
        /// Perlin-Worley: inverted cellular noise gives the rounded, billowy lobes that read
        /// as cumulus, and value-noise fBm breaks up the edges so they aren't obviously
        /// cellular. Plain fBm alone produces soft grey clumps that don't read as cloud.
        /// </summary>
        private void Generate(int seed)
        {
            // A larger map's periods grow with it: the same cells per metre, so the same clouds.
            int n = Resolution;
            int basePeriod = BasePeriod * _scale;

            // A larger map is LargerScale^2 the work, and the city's first map is built on the main
            // thread at load: its rows are split over the cores. The usual map: one thread, as always.
            if (_scale == 1)
            {
                GenerateRows(seed, 0, n, n, basePeriod);
                BuildThreads = 1;
            }
            else
            {
                BuildThreads = ParallelRows.Run(n, "VolumetricClouds weather map", (first, end) => GenerateRows(seed, first, end, n, basePeriod));
            }

            Normalise();
            BuildThresholdTable();
        }

        /// <summary>Rows <paramref name="y0"/> to <paramref name="y1"/> (exclusive). Each texel is its own: any thread may do any rows.</summary>
        private void GenerateRows(int seed, int y0, int y1, int n, int basePeriod)
        {
            for (int y = y0; y < y1; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float value = ValueFbm(x, y, seed, n, basePeriod);
                    float billow = WorleyFbm(x, y, seed, n, basePeriod);

                    // Remap the value noise by the billow floor: where billow is high the
                    // cloud is solid, where it is low the value noise carves it away.
                    // The lower bound is billow-1 rather than 1-billow so the divisor is
                    // 2-billow, bounded to [1,2]. Dividing by billow itself lets the result
                    // explode wherever billow approaches zero, and those outliers squash
                    // the whole field once it is normalised.
                    _density[y * n + x] = Remap(value, billow - 1f, 1f, 0f, 1f);
                }
            }
        }

        private static float ValueFbm(int x, int y, int seed, int n, int basePeriod)
        {
            float sum = 0f;
            float amplitude = 1f;
            float total = 0f;
            int period = basePeriod;

            for (int octave = 0; octave < ValueOctaves; octave++)
            {
                sum += ValueNoise(x * period / (float)n, y * period / (float)n,
                                  period, seed + octave) * amplitude;
                total += amplitude;
                amplitude *= 0.5f;
                period *= 2;
            }

            return sum / total;
        }

        private static float WorleyFbm(int x, int y, int seed, int n, int basePeriod)
        {
            float sum = 0f;
            float amplitude = 1f;
            float total = 0f;
            int period = basePeriod;

            for (int octave = 0; octave < WorleyOctaves; octave++)
            {
                // Inverted: near a feature point means dense, not sparse.
                float worley = 1f - Worley(x * period / (float)n, y * period / (float)n,
                                           period, seed + 101 + octave);
                sum += worley * amplitude;
                total += amplitude;
                amplitude *= 0.5f;
                period *= 2;
            }

            return Mathf.Clamp01(sum / total);
        }

        /// <summary>Distance to the nearest feature point, on a periodic cell grid so it tiles.</summary>
        private static float Worley(float x, float y, int period, int seed)
        {
            int xi = Mathf.FloorToInt(x);
            int yi = Mathf.FloorToInt(y);
            float best = float.MaxValue;

            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = Wrap(xi + dx, period);
                    int cy = Wrap(yi + dy, period);

                    float px = xi + dx + Hash(cx, cy, seed);
                    float py = yi + dy + Hash(cx, cy, seed + 7919);

                    float ox = px - x;
                    float oy = py - y;
                    float d = ox * ox + oy * oy;

                    if (d < best)
                        best = d;
                }
            }

            return Mathf.Clamp01(Mathf.Sqrt(best));
        }

        private static float ValueNoise(float x, float y, int period, int seed)
        {
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fx = x - x0;
            float fy = y - y0;

            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);

            int x1 = Wrap(x0 + 1, period);
            int y1 = Wrap(y0 + 1, period);
            x0 = Wrap(x0, period);
            y0 = Wrap(y0, period);

            float top = Mathf.Lerp(Hash(x0, y0, seed), Hash(x1, y0, seed), fx);
            float bottom = Mathf.Lerp(Hash(x0, y1, seed), Hash(x1, y1, seed), fx);
            return Mathf.Lerp(top, bottom, fy);
        }

        /// <summary>
        /// Stretches the field to the full 0..1 range. Without this the Perlin-Worley remap
        /// leaves it bunched in the middle and the coverage slider does nothing at the ends.
        /// </summary>
        private void Normalise()
        {
            float min = float.MaxValue;
            float max = float.MinValue;

            for (int i = 0; i < _density.Length; i++)
            {
                if (_density[i] < min)
                    min = _density[i];
                if (_density[i] > max)
                    max = _density[i];
            }

            float span = max - min;
            if (span < 1e-5f)
                return;

            for (int i = 0; i < _density.Length; i++)
                _density[i] = (_density[i] - min) / span;
        }

        private static float Remap(float value, float fromMin, float fromMax, float toMin, float toMax)
        {
            float span = fromMax - fromMin;
            if (Mathf.Abs(span) < 1e-5f)
                return toMin;

            return toMin + (value - fromMin) / span * (toMax - toMin);
        }

        private static int Wrap(int v, int period)
        {
            int r = v % period;
            return r < 0 ? r + period : r;
        }

        private static float Hash(int x, int y, int seed)
        {
            int n = x * 374761393 + y * 668265263 + seed * 1274126177;
            n = (n ^ (n >> 13)) * 1274126177;
            n = n ^ (n >> 16);
            return (n & 0x7fffffff) / (float)0x7fffffff;
        }
    }
}
