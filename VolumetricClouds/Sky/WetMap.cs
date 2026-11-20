using ColossalFramework;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// Where the ground is wet (1.6.1): a map of the city, each cell its share of the game's ground wetness, so wet
    /// roads, puddles and wheel spray are only where it has rained (his report 2026-11-20: wet everywhere under a
    /// clear sky -- the game keeps ONE wetness for the whole map). The rule per cell is the game's own
    /// (<see cref="WetMapRules"/>) with the rain that falls there: m_currentRain x CloudRain.LocalRain.
    /// </summary>
    /// <remarks>
    /// World-fixed: <see cref="Size"/>^2 cells over the 17.28 km map (135 m), the same square TerrainHeightMap covers
    /// (WetGround.shader reads both at wpos.xz / 17280 + 0.5). ARGB32, linear, bilinear, clamped: the share in every
    /// channel (WetGround.shader reads r). Not saved: a city starts with the game's wetness everywhere (share 1).
    ///
    /// Cheap: <see cref="RowsPerFrame"/> rows a frame on the main thread (CloudRain.LocalRain and the ground height per
    /// cell), the whole map in about two seconds, uploaded once per sweep in a frame of its own. Each row runs the rule over the
    /// simulation frames gone by since its last visit (SimulationManager.m_currentFrameIndex): frozen while the game is
    /// paused, faster at a higher speed, exactly as the game's own number moves.
    ///
    /// "Rain sound, wet roads and road snow follow the clouds" off, our rain off, the weather hidden or a winter map:
    /// every cell is the game's number (share 1 everywhere: the look of 1.6.0, to the bit). The rain's height above the
    /// ground matters: a drop falls slanted, so the rain over a cell left the cloud base hundreds of metres upwind;
    /// LocalRain is asked at the cell's own ground height.
    ///
    /// Invariant 2: the game's wetness is read, never written.
    /// </remarks>
    public static class WetMap
    {
        public const int Size = 128;
        private const float MapSide = 17280f;
        /// <summary>
        /// Rows a frame (1.6.1: 2, was 8 -- his log had 0.06-0.37 ms of the main thread a frame for 8). The rule is exact over
        /// any number of frames (each row runs it over the simulation frames since its last visit), so fewer rows only
        /// lengthen the sweep: 64 frames plus one for its upload and one for the row whose ground is re-read, ~2.2 s at 30
        /// fps (0.55 s before). What a longer sweep changes is only how often each cell's rain is looked at: the rain applied
        /// over the frames since a cell's last visit is the rain there at the visit, and in 2.2 s the clouds carry it about
        /// 46 m at his 21 m/s, a third of a cell's 135 m.
        /// </summary>
        private const int RowsPerFrame = 2;
        /// <summary>More simulation frames than this between two visits of a row counts as none (a reset of the frame index).</summary>
        private const uint MostFrames = 600000u;

        private static float[] _cells;          // each cell's wetness, 0..1 (absolute, the game's units)
        private static float[] _share;          // each cell's share of the game's wetness, 0..1 (what is read)
        private static float[] _ground;         // each cell's ground height (CloudRain.LocalRain slants by the height under the base)
        private static bool[] _groundRead;      // per row: its heights have been read
        private static bool _allDry;
        private static double _frameMs, _frameMsMost;
        private static uint[] _rowFrame;        // the simulation frame each row was last brought up to
        private static Color32[] _pixels;
        private static Texture2D _texture;
        private static int _nextRow;
        private static bool _following;
        private static string _loggedState;
        private static float _nextLog;
        private static int _sweeps;

        /// <summary>The map as drawn (null until the first sweep while following; WetGround binds white then).</summary>
        public static Texture2D Texture
        {
            get { return _following ? _texture : null; }
        }

        /// <summary>True while the cells follow the rain by place (else every share is 1).</summary>
        public static bool Following
        {
            get { return _following; }
        }

        /// <summary>
        /// Once a frame (WetGround.LateUpdate): <paramref name="wanted"/> = something reads the map (a wet row or the
        /// spray's over 0). Not wanted: nothing runs and the map is let go; wanted again: it starts again from the
        /// game's number everywhere.
        /// </summary>
        public static void Update(bool wanted)
        {
            if (!wanted || !Singleton<WeatherManager>.exists || !Singleton<SimulationManager>.exists)
            {
                Release(!wanted ? "nothing reads it (the wet rows and wheel spray are at 0%)" : "no weather");
                return;
            }

            float game = WetGround.GroundWetness;
            uint frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
            bool follow = CloudRain.Active && !CloudRain.IsSnow && (Settings.RainLocalised == null || Settings.RainLocalised.value);

            if (!follow)
            {
                if (_following)
                    State("the game's one wetness everywhere (" + (CloudRain.IsSnow ? "a winter map" : !CloudRain.Active ? "our rain is off or the weather hidden" : "\"follow the clouds\" is off") + ")");
                // Share 1 everywhere (At, Texture: white in WetGround); seeded afresh when it follows again.
                _following = false;
                return;
            }

            if (_cells == null || !_following)
            {
                Seed(game, frame);
                _following = true;
                State("following the rain by place (" + Size + "x" + Size + " cells of " + (MapSide / Size).ToString("F0") +
                      " m over the map, the game's rule per cell; started from the game's " + (game * 100f).ToString("F0") + "% everywhere)");
            }

            float rainNow = Mathf.Clamp01(Singleton<WeatherManager>.instance.m_currentRain);

            // The game's ground dry and no rain falling: every cell is 0 (never wetter than the game's), nothing to do per
            // cell. The rows are kept at this frame, so the first rain after starts from here.
            if (game <= 0f && rainNow <= 0f)
            {
                if (!_allDry)
                {
                    _allDry = true;
                    for (int i = 0; i < _cells.Length; i++)
                    {
                        _cells[i] = 0f;
                        _share[i] = 1f;
                        _pixels[i] = new Color32(255, 255, 255, 255);
                    }
                    if (_texture != null)
                        Upload();
                }
                for (int r = 0; r < Size; r++)
                    _rowFrame[r] = frame;
                return;
            }
            _allDry = false;

            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            if (_uploadDue)
            {
                // The sweep that just ended, uploaded in a frame of its own (1.6.1): never on top of the rows' work. Not at all
                // when no cell's byte changed over the sweep (rain steady over dry or soaked ground).
                _uploadDue = false;
                if (_sweepChanged)
                    Upload();
                _sweepChanged = false;
                double up = clock.Elapsed.TotalMilliseconds;
                _uploadMs = up;
                _uploadMsMost = System.Math.Max(_uploadMsMost, up);
            }
            else
            {
                TerrainManager terrain = Singleton<TerrainManager>.exists ? Singleton<TerrainManager>.instance : null;
                float cell = MapSide / Size;
                for (int k = 0; k < RowsPerFrame; k++)
                {
                    int row = _nextRow;
                    // The ground's height under each cell: read on the first visit, then one row again per sweep (all of them
                    // in about 5 minutes at 30 fps: terrain is changed by hand, slowly). A row that reads its ground has the
                    // frame to itself (1.6.1): 128 more height lookups.
                    bool readGround = !_groundRead[row] || row == _sweeps % Size;
                    if (readGround && k > 0)
                        break;
                    uint frames = WetMapRules.FramesSince(_rowFrame[row], frame, MostFrames);
                    _rowFrame[row] = frame;
                    float z = (row + 0.5f) * cell - MapSide * 0.5f;
                    for (int col = 0; col < Size; col++)
                    {
                        int i = row * Size + col;
                        float x = (col + 0.5f) * cell - MapSide * 0.5f;
                        if (readGround)
                            _ground[i] = terrain != null ? terrain.SampleRawHeightSmooth(new Vector3(x, 0f, z)) : 0f;
                        float w = _cells[i];
                        if (frames > 0u)
                            w = WetMapRules.Step(w, rainNow * CloudRain.LocalRain(new Vector3(x, _ground[i], z)), frames);
                        w = WetMapRules.Follow(w, game);
                        _cells[i] = w;
                        float share = WetMapRules.Ratio(w, game);
                        _share[i] = share;
                        byte b = (byte)Mathf.RoundToInt(share * 255f);
                        if (_pixels[i].r != b)
                        {
                            _pixels[i] = new Color32(b, b, b, b);
                            _sweepChanged = true;
                        }
                    }

                    _groundRead[row] = true;
                    _nextRow = (_nextRow + 1) % Size;
                    if (_nextRow == 0)
                    {
                        _uploadDue = true;
                        _sweeps++;
                        break;
                    }
                    if (readGround)
                        break;
                }
                double ms = clock.Elapsed.TotalMilliseconds;
                _frameMs = ms;
                _frameMsMost = System.Math.Max(_frameMsMost, ms);
            }

            if (Log.Detailed && Time.time >= _nextLog)
            {
                _nextLog = Time.time + 10f;
                float least = 1f, sum = 0f;
                for (int i = 0; i < _share.Length; i++)
                {
                    least = Mathf.Min(least, _share[i]);
                    sum += _share[i];
                }
                Log.Detail("wet map: the game's wetness " + (game * 100f).ToString("F0") + "%, rain " + (rainNow * 100f).ToString("F0") +
                           "%; the cells' share of it: mean " + (sum / _share.Length * 100f).ToString("F0") + "%, least " +
                           (least * 100f).ToString("F0") + "%; " + _sweeps + " sweeps; " + RowsPerFrame + " rows a frame took " +
                           _frameMs.ToString("F3") + " ms (the most since the last line " + _frameMsMost.ToString("F3") + " ms), the upload " +
                           _uploadMs.ToString("F3") + " ms (most " + _uploadMsMost.ToString("F3") + " ms, its own frame once a sweep)");
                _frameMsMost = 0.0;
                _uploadMsMost = 0.0;
            }
        }

        private static bool _uploadDue, _sweepChanged;
        private static double _uploadMs, _uploadMsMost;

        /// <summary>The share of the game's wetness at <paramref name="position"/> (bilinear), 1 when the map is not following.</summary>
        public static float At(Vector3 position)
        {
            if (!_following || _share == null)
                return 1f;

            float fx = (position.x / MapSide + 0.5f) * Size - 0.5f;
            float fz = (position.z / MapSide + 0.5f) * Size - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, Size - 1), z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, Size - 1);
            int x1 = Mathf.Min(x0 + 1, Size - 1), z1 = Mathf.Min(z0 + 1, Size - 1);
            float tx = Mathf.Clamp01(fx - x0), tz = Mathf.Clamp01(fz - z0);
            float a = Mathf.Lerp(_share[z0 * Size + x0], _share[z0 * Size + x1], tx);
            float b = Mathf.Lerp(_share[z1 * Size + x0], _share[z1 * Size + x1], tx);
            return Mathf.Lerp(a, b, tz);
        }

        /// <summary>Every cell = the game's number now (share 1), every row brought up to this frame.</summary>
        private static void Seed(float game, uint frame)
        {
            if (_cells == null)
            {
                _cells = new float[Size * Size];
                _share = new float[Size * Size];
                _rowFrame = new uint[Size];
                _pixels = new Color32[Size * Size];
                _ground = new float[Size * Size];
                _groundRead = new bool[Size];
            }
            _allDry = false;

            for (int i = 0; i < _cells.Length; i++)
            {
                _cells[i] = game;
                _share[i] = 1f;
                _pixels[i] = new Color32(255, 255, 255, 255);
            }
            for (int r = 0; r < Size; r++)
                _rowFrame[r] = frame;
            _nextRow = 0;
            _uploadDue = false;
            _sweepChanged = false;
            // A texture left from before shows the share 1 at once, not the old map until the first sweep ends.
            if (_texture != null)
                Upload();
        }

        private static void Upload()
        {
            if (_texture == null)
            {
                _texture = new Texture2D(Size, Size, TextureFormat.ARGB32, false, true)
                {
                    name = "VolumetricCloudsWetMap",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 0,
                };
            }
            _texture.SetPixels32(_pixels);
            _texture.Apply(false);
        }

        private static void State(string what)
        {
            if (what == _loggedState)
                return;
            _loggedState = what;
            Log.Msg("wet map: " + what);
        }

        /// <summary>Lets the map go (nothing reads it, or the city closes).</summary>
        public static void Release(string why)
        {
            if (_cells == null && _texture == null)
                return;
            _cells = null;
            _share = null;
            _rowFrame = null;
            _pixels = null;
            _ground = null;
            _groundRead = null;
            _following = false;
            if (_texture != null)
                Object.Destroy(_texture);
            _texture = null;
            if (why != null)
                State("let go -- " + why);
        }
    }
}
