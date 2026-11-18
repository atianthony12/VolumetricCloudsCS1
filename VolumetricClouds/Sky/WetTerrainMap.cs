using System;
using System.Threading;
using ColossalFramework;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// Where the wet ground pass sees bare terrain (2026-11-18, the author: "on terrain it's making the
    /// game look super washed out ... wet ground should be mainly for roads/surfaces, and terrain only
    /// gets around 10% of the wetness value"). A camera-centred map of the game's own terrain surface.
    /// </summary>
    /// <remarks>
    /// Nothing in the picture tells terrain from a road: both write the same normals alpha (1) and the
    /// same decal stencil (Ref 1). The game's terrain surface does: every road, and every building that
    /// asks, CLIPS the terrain under it (NetInfo / BuildingInfo m_clipTerrain -> TerrainModify.ApplyQuad
    /// writes SurfaceCell.m_clipped, 0..255 = the share of the cell), and paved and gravel lots and gravel
    /// roads are drawn by the terrain from its m_pavementA/B and m_gravel. So "bare terrain" here =
    /// unclipped and unpaved (<see cref="BareShare"/>); fields, ruins, sand and grass all count.
    ///
    /// Top-down, a roof or a car over grass reads as grass too: the map also holds the ground's height,
    /// and the shader counts a pixel as terrain only when it lies on the ground.
    ///
    /// ONE RGBAHalf texture, <see cref="Size"/> square, two maps round one centre (where the camera looks):
    /// rg = NEAR, the game's own 4 m detail cells over <see cref="NearSpan"/> m (r height above the centre's
    /// <see cref="BaseHeight"/>, so a half float keeps it to ~0.25 m; g bare share), ba = FAR, 16 m cells over
    /// <see cref="FarSpan"/> m (raw heights there). Built on a worker from the game's arrays (read only,
    /// as the game's render thread reads them), uploaded on the main thread; again when the centre moves
    /// a sixth of the near span, and every <see cref="RefreshSeconds"/> s for new roads and lots.
    /// Only while the wet pass draws (off means nothing runs).
    /// </remarks>
    public sealed class WetTerrainMap
    {
        public const int Size = 512;
        public const float NearCell = 4f;
        public const float FarCell = 16f;
        public const float NearSpan = Size * NearCell;   // 2048 m
        public const float FarSpan = Size * FarCell;     // 8192 m
        private const float RefreshSeconds = 30f;
        private const float MoveAgain = NearSpan / 6f;

        private Texture2D _texture;
        private Color[] _pixels;
        private Color[] _building;
        private volatile bool _busy;
        private volatile bool _done;
        private volatile string _error;
        private Vector2 _pendingCentre;
        private float _pendingBase;
        private float _pendingMs;
        private float _pendingBare;
        private float _nextRefresh;
        private bool _failed;
        private bool _logged;

        /// <summary>The uploaded map (null until the first build is in).</summary>
        public Texture Texture { get { return _texture != null && Ready ? _texture : null; } }

        public bool Ready { get; private set; }

        /// <summary>World x, z of the centre of both maps.</summary>
        public Vector2 Centre { get; private set; }

        /// <summary>The ground's height at the centre: the maps' heights are above (or below) it.</summary>
        public float BaseHeight { get; private set; }

        /// <summary>
        /// The share of a 4 m cell that is bare terrain: not clipped by a road or building, not paved,
        /// not gravel. Pure.
        /// </summary>
        public static float BareShare(byte clipped, byte pavementA, byte pavementB, byte gravel)
        {
            float clip = clipped / 255f;
            float surface = Mathf.Clamp01((pavementA + pavementB + gravel) / 255f);
            return (1f - clip) * (1f - surface);
        }

        /// <summary>Keeps the map round <paramref name="lookAt"/>; call once a frame while the wet pass draws.</summary>
        public void Update(Vector3 lookAt)
        {
            if (_failed || !Singleton<TerrainManager>.exists)
                return;

            if (_done)
            {
                _done = false;
                _busy = false;
                if (_error != null)
                {
                    _failed = true;
                    Log.Msg("wet ground: the terrain map could not be built (" + _error + "): roads and terrain stay equally wet");
                    return;
                }
                Upload();
            }

            if (_busy)
                return;

            Vector2 want = new Vector2(Mathf.Round(lookAt.x / FarCell) * FarCell, Mathf.Round(lookAt.z / FarCell) * FarCell);
            bool moved = !Ready || (want - Centre).magnitude > MoveAgain;
            if (!moved && Time.realtimeSinceStartup < _nextRefresh)
                return;

            _nextRefresh = Time.realtimeSinceStartup + RefreshSeconds;
            if (_building == null)
                _building = new Color[Size * Size];
            _pendingCentre = want;
            _pendingBase = Singleton<TerrainManager>.instance.SampleRawHeightSmooth(new Vector3(want.x, 0f, want.y));
            _error = null;
            _busy = true;
            ThreadPool.QueueUserWorkItem(Build);
        }

        private void Build(object state)
        {
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                TerrainManager terrain = Singleton<TerrainManager>.instance;
                Vector2 centre = _pendingCentre;
                float baseHeight = _pendingBase;
                float half = TerrainHeightMap.MapSize * 0.5f;
                double bareSum = 0.0;
                for (int z = 0; z < Size; z++)
                {
                    for (int x = 0; x < Size; x++)
                    {
                        // Near: the texel's centre, its own 4 m cell.
                        float nx = centre.x + (x + 0.5f - Size * 0.5f) * NearCell;
                        float nz = centre.y + (z + 0.5f - Size * 0.5f) * NearCell;
                        float nearHeight = 0f, nearBare = 1f;
                        if (Mathf.Abs(nx) < half && Mathf.Abs(nz) < half)
                        {
                            nearHeight = terrain.SampleDetailHeight(new Vector3(nx, 0f, nz)) - baseHeight;
                            nearBare = Bare(terrain, nx, nz);
                        }

                        // Far: one 4 m cell at the 16 m texel's centre.
                        float fx = centre.x + (x + 0.5f - Size * 0.5f) * FarCell;
                        float fz = centre.y + (z + 0.5f - Size * 0.5f) * FarCell;
                        float farHeight = 0f, farBare = 1f;
                        if (Mathf.Abs(fx) < half && Mathf.Abs(fz) < half)
                        {
                            farHeight = terrain.SampleRawHeightSmooth(new Vector3(fx, 0f, fz)) - baseHeight;
                            farBare = Bare(terrain, fx, fz);
                        }

                        _building[z * Size + x] = new Color(nearHeight, nearBare, farHeight, farBare);
                        bareSum += nearBare;
                    }
                }
                _pendingMs = (float)watch.Elapsed.TotalMilliseconds;
                _pendingBare = (float)(bareSum / (Size * Size));
            }
            catch (Exception e)
            {
                _error = e.GetType().Name + ": " + e.Message;
            }
            _done = true;
        }

        // The game's own sampler (IL: x / 4 + 2160, bilinear between the 4 m cells, the clip flag too): a
        // cell's value sits ON its lattice point, so reading cell[floor] at a texel's centre would shift the
        // roads 2 m.
        private static float Bare(TerrainManager terrain, float x, float z)
        {
            TerrainManager.SurfaceCell cell = terrain.SampleDetailSurface(new Vector3(x, 0f, z));
            return BareShare(cell.m_clipped, cell.m_pavementA, cell.m_pavementB, cell.m_gravel);
        }

        private void Upload()
        {
            if (_texture == null)
            {
                TextureFormat format = SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf) ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat;
                _texture = new Texture2D(Size, Size, format, false, true)
                {
                    name = "VolumetricCloudsWetTerrain",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }

            // Swap: the worker fills one array while the texture keeps the other.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Color[] filled = _building;
            _building = _pixels;
            _pixels = filled;
            _texture.SetPixels(_pixels);
            _texture.Apply(false);
            float uploadMs = (float)watch.Elapsed.TotalMilliseconds;
            Centre = _pendingCentre;
            BaseHeight = _pendingBase;
            Ready = true;

            if (!_logged)
            {
                _logged = true;
                Log.Msg("wet ground: terrain map built round (" + Centre.x.ToString("F0") + ", " + Centre.y.ToString("F0") + ") in " +
                        _pendingMs.ToString("F0") + " ms on a worker, uploaded in " + uploadMs.ToString("F1") +
                        " ms on the main thread: " + (_pendingBare * 100f).ToString("F0") +
                        "% of the near " + NearSpan.ToString("F0") + " m is bare terrain (Wet grass and soil), the rest roads, lots and paving; far map " + FarSpan.ToString("F0") + " m");
            }
            else if (Log.Detailed)
            {
                Log.Detail("wet ground: terrain map rebuilt round (" + Centre.x.ToString("F0") + ", " + Centre.y.ToString("F0") + ") in " +
                           _pendingMs.ToString("F0") + " ms (upload " + uploadMs.ToString("F1") + " ms), " +
                           (_pendingBare * 100f).ToString("F0") + "% bare terrain near");
            }
        }

        /// <summary>Drops the texture; a build still running finishes into nothing.</summary>
        public void Destroy()
        {
            if (_texture != null)
                UnityEngine.Object.Destroy(_texture);
            _texture = null;
            Ready = false;
        }
    }
}
