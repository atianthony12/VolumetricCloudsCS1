using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// What a strong wind carries near the camera (2026-10-30; the author: "I still don't see leaves
    /// flying around if wind is too strong, dust kicking a bit"): leaves torn off the trees and blown
    /// out of the woods, and dust -- blowing snow on a winter map -- lifted off dry open ground. Two
    /// draws of WindDebris.shader, after the air like the waterspouts' flecks (FunnelDebris); the
    /// shader flies every particle from a seed, on the trees' own wind (TreeShaders: its strength,
    /// direction and clock), so they go the way the trees bend. Only while the trees carry VW's wind.
    /// The game's data is read into a small map round the point the camera looks at, once a second:
    /// 32 x 32 of the tree grid's own 32 m cells -- trees per cell and their height (TreeManager's
    /// grid), the ground's or water's height, and how dusty the ground is (GroundUnder's kind of
    /// ground: none on water, little in a wood or on pavement). All visual: nothing here touches the
    /// game's trees or anything else.
    /// </summary>
    public class WindDebris : MonoBehaviour
    {
        private const int LeafCount = 3000;
        private const int DustCount = 500;

        /// <summary>The tiles they are spread over round the focus, metres (faded over their outer part).</summary>
        private const float LeafTile = 300f;
        private const float DustTile = 400f;

        /// <summary>The map: cells a side, and their size (the game's tree grid's).</summary>
        private const int MapCells = 32;
        private const float CellSize = TreeManager.TREEGRID_CELL_SIZE;
        private const int GridResolution = TreeManager.TREEGRID_RESOLUTION;

        /// <summary>Trees in a 32 m cell that make it a wood (r = 1).</summary>
        private const float WoodTrees = 5f;

        /// <summary>
        /// The wind (breezes, after the rows) where leaves begin and where they are all out; the same for
        /// dust. Storms only (the author, 2026-10-30: "only at high winds ... It needs to be stormy at
        /// least"): TreeWind.Describe's "stormy" starts at 2.
        /// </summary>
        private const float LeavesFrom = 2f, LeavesFull = 3f;
        private const float DustFrom = 2.2f, DustFull = 3.4f;

        private const float MapInterval = 1f;

        private static readonly int IdPart = Shader.PropertyToID("_Part");
        private static readonly int IdFocus = Shader.PropertyToID("_WVFocus");
        private static readonly int IdMapPlace = Shader.PropertyToID("_WVMapPlace");
        private static readonly int IdMap = Shader.PropertyToID("_WVMap");
        private static readonly int IdWind = Shader.PropertyToID("_WVWind");
        private static readonly int IdClock = Shader.PropertyToID("_WVClock");
        private static readonly int IdColor = Shader.PropertyToID("_WVColor");
        private static readonly int IdLook = Shader.PropertyToID("_WVLook");
        private static readonly int IdAmbient = Shader.PropertyToID("_WVAmbient");
        private static readonly int IdSun = Shader.PropertyToID("_WVSun");
        private static readonly int IdPixelAngle = Shader.PropertyToID("_PixelAngle");

        private Material _leaves;
        private Material _dust;
        private Mesh _leafMesh;
        private Mesh _dustMesh;
        private Texture2D _map;
        private Color[] _pixels;
        private Camera _camera;
        private CameraController _controller;
        private bool _lookedForController;

        private int _mapX0 = int.MinValue, _mapZ0 = int.MinValue;
        private float _mapFocusY;
        private float _nextMap;
        private int _trees, _dustyCells;
        private float _nextLog;
        private bool _drawing;

        private void Start()
        {
            Shader shader = ShaderBundle.Get(ShaderBundle.WindDebris);
            if (shader == null)
            {
                Log.Warn("wind debris: its shader is missing from the bundle; no leaves or dust fly");
                enabled = false;
                return;
            }

            _leaves = new Material(shader) { name = "VolumetricCloudsWindLeaves", renderQueue = CloudVolume.AirQueue + 1 };
            _leaves.SetFloat(IdPart, 0f);
            _dust = new Material(shader) { name = "VolumetricCloudsWindDust", renderQueue = CloudVolume.AirQueue + 1 };
            _dust.SetFloat(IdPart, 1f);
            _leafMesh = BuildSeeds(LeafCount, 20261030, "VolumetricCloudsWindLeaves");
            _dustMesh = BuildSeeds(DustCount, 20261031, "VolumetricCloudsWindDust");

            TextureFormat format = SystemInfo.SupportsTextureFormat(TextureFormat.RGBAHalf) ? TextureFormat.RGBAHalf : TextureFormat.RGBAFloat;
            _map = new Texture2D(MapCells, MapCells, format, false, true)
            {
                name = "VolumetricCloudsWindDebrisMap",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _pixels = new Color[MapCells * MapCells];
            _leaves.SetTexture(IdMap, _map);
            _dust.SetTexture(IdMap, _map);
        }

        private void LateUpdate()
        {
            try
            {
                Draw();
            }
            catch (Exception e)
            {
                Log.Error("wind debris: failed; no leaves or dust fly for the rest of this city", e);
                enabled = false;
            }
        }

        private void Draw()
        {
            if (_leaves == null || !TreeShaders.Active)
            {
                Stopped();
                return;
            }

            TreeWind.Wind wind = TreeShaders.Current;
            bool winter = GroundUnder.Winter;
            // Wet ground gives no dust (snow still blows); wet leaves fly less.
            float rain = CloudWeather.Rain;
            float leaves = Row(Settings.TreeLeaves, Settings.Defaults.TreeLeaves) * Ramp(LeavesFrom, LeavesFull, wind.Speed) * (1f - 0.5f * rain);
            float dust = Row(Settings.TreeDust, Settings.Defaults.TreeDust) * Ramp(DustFrom, DustFull, wind.Speed) *
                         (winter ? 1f : 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.3f, rain)));
            if (leaves <= 0.005f && dust <= 0.005f)
            {
                Stopped();
                return;
            }

            if (_camera == null)
                _camera = Camera.main;
            if (_camera == null)
                return;

            Vector3 focus = Focus();
            if (Time.time >= _nextMap || Moved(focus))
            {
                _nextMap = Time.time + MapInterval;
                BuildMap(focus);
            }

            TreeWind.Motion clock = TreeShaders.Clock;
            Vector3 dir = TreeShaders.Blowing;
            float pixelAngle = 2f * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, _camera.pixelHeight);
            Vector4 focusVector = new Vector4(focus.x, _mapFocusY, focus.z, 0f);
            Vector4 place = new Vector4(_mapX0 * CellSize - TreeManager.TREEGRID_RESOLUTION * 0.5f * CellSize,
                                        _mapZ0 * CellSize - TreeManager.TREEGRID_RESOLUTION * 0.5f * CellSize,
                                        1f / (MapCells * CellSize), 0f);
            // The leaves and the dust travel at the gusts' speed (the trees' wind's), the dust a little faster.
            float fog = FunnelDebris.FogInFront((focus - _camera.transform.position).magnitude);
            Vector4 ambient = AsVector(Funnels.AirAmbient);
            Vector4 sun = AsVector(Funnels.Sun);

            if (leaves > 0.005f)
            {
                Set(_leaves, focusVector, place, pixelAngle, ambient, sun);
                _leaves.SetVector(IdWind, new Vector4(dir.x, dir.z, wind.GustSpeed, Mathf.Clamp01(leaves)));
                _leaves.SetVector(IdClock, new Vector4(clock.Phase, clock.Epoch, (float)TreeWind.ClockPeriod, LeafTile));
                _leaves.SetVector(IdColor, new Vector4(0.20f, 0.19f, 0.07f, 0.95f * fog * Mathf.Min(1f, leaves)));
                // 12 cm across, a leaf's length 1.6 of that: small enough to read as leaves in the wind,
                // not as shapes (the author: "make them smaller so the user doesn't see them directly").
                _leaves.SetVector(IdLook, new Vector4(0.12f, 1.6f, 0.9f, 0f));
                Graphics.DrawMesh(_leafMesh, Matrix4x4.identity, _leaves, 0, null, 0);
            }

            if (dust > 0.005f)
            {
                Color colour = winter ? new Color(0.90f, 0.92f, 0.95f) : new Color(0.42f, 0.36f, 0.27f);
                Set(_dust, focusVector, place, pixelAngle, ambient, sun);
                _dust.SetVector(IdWind, new Vector4(dir.x, dir.z, wind.GustSpeed, Mathf.Clamp01(dust)));
                _dust.SetVector(IdClock, new Vector4(clock.Phase, clock.Epoch, (float)TreeWind.ClockPeriod, DustTile));
                _dust.SetVector(IdColor, new Vector4(colour.r, colour.g, colour.b, (winter ? 0.16f : 0.11f) * fog * Mathf.Min(1.5f, dust)));
                _dust.SetVector(IdLook, new Vector4(1f, 1.8f, 0f, 0f));
                Graphics.DrawMesh(_dustMesh, Matrix4x4.identity, _dust, 0, null, 0);
            }

            if (!_drawing)
            {
                _drawing = true;
                Log.Msg("wind debris: " + Describe(wind, leaves, dust, winter));
            }
            else if (Log.Detailed && Time.time >= _nextLog)
            {
                _nextLog = Time.time + 5f;
                Log.Detail("wind debris: " + Describe(wind, leaves, dust, winter));
            }
        }

        private string Describe(TreeWind.Wind wind, float leaves, float dust, bool winter)
        {
            return "leaves " + (100f * leaves).ToString("F0") + "%, " + (winter ? "blowing snow " : "dust ") + (100f * dust).ToString("F0") +
                   "% in a wind of " + wind.Speed.ToString("F2") + " breezes (" + TreeWind.Describe(wind.Speed) + "), travelling " +
                   wind.GustSpeed.ToString("F0") + " m/s | round the focus: " + _trees + " trees, " + _dustyCells + " of " +
                   (MapCells * MapCells) + " cells of open dry ground";
        }

        private void Stopped()
        {
            if (_drawing)
            {
                _drawing = false;
                Log.Msg("wind debris: none (the wind dropped, the rows are at 0, or the trees are on the game's sway)");
            }
        }

        private void Set(Material m, Vector4 focus, Vector4 place, float pixelAngle, Vector4 ambient, Vector4 sun)
        {
            m.SetVector(IdFocus, focus);
            m.SetVector(IdMapPlace, place);
            m.SetFloat(IdPixelAngle, pixelAngle);
            m.SetVector(IdAmbient, ambient);
            m.SetVector(IdSun, sun);
        }

        private static float Row(FloatSetting s, float fallback)
        {
            return Mathf.Max(0f, s != null ? s.value : fallback);
        }

        private static float Ramp(float from, float full, float speed)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(from, full, speed));
        }

        private bool Moved(Vector3 focus)
        {
            return CellOf(focus.x) - MapCells / 2 != _mapX0 || CellOf(focus.z) - MapCells / 2 != _mapZ0;
        }

        private static int CellOf(float v)
        {
            return Mathf.FloorToInt(v / CellSize + GridResolution * 0.5f);
        }

        /// <summary>
        /// The map round the focus: per 32 m cell of the tree grid, r = trees (a wood = 1), g = their
        /// mean height (m), b = the ground's or water's height over the focus's (m), a = how dusty.
        /// </summary>
        private void BuildMap(Vector3 focus)
        {
            _mapX0 = CellOf(focus.x) - MapCells / 2;
            _mapZ0 = CellOf(focus.z) - MapCells / 2;
            TerrainManager terrain = Singleton<TerrainManager>.exists ? Singleton<TerrainManager>.instance : null;
            _mapFocusY = terrain != null ? terrain.SampleRawHeightSmoothWithWater(focus, false, 0f) : focus.y;

            TreeManager trees = Singleton<TreeManager>.exists ? Singleton<TreeManager>.instance : null;
            uint[] grid = trees != null ? trees.m_treeGrid : null;
            TreeInstance[] buffer = trees != null && trees.m_trees != null ? trees.m_trees.m_buffer : null;
            int total = 0, dusty = 0;

            for (int z = 0; z < MapCells; z++)
            {
                for (int x = 0; x < MapCells; x++)
                {
                    int gx = _mapX0 + x, gz = _mapZ0 + z;
                    Vector3 centre = new Vector3((gx + 0.5f - GridResolution * 0.5f) * CellSize, 0f, (gz + 0.5f - GridResolution * 0.5f) * CellSize);
                    int count = 0;
                    float heights = 0f;
                    if (grid != null && buffer != null && gx >= 0 && gz >= 0 && gx < GridResolution && gz < GridResolution)
                    {
                        uint id = grid[gz * GridResolution + gx];
                        int guard = 0;
                        while (id != 0u && id < buffer.Length && guard++ < 4096)
                        {
                            TreeInstance tree = buffer[id];
                            if ((tree.m_flags & (ushort)(TreeInstance.Flags.Created | TreeInstance.Flags.Deleted | TreeInstance.Flags.Hidden)) ==
                                (ushort)TreeInstance.Flags.Created)
                            {
                                TreeInfo info = tree.Info;
                                if (info != null)
                                {
                                    count++;
                                    float size = info.m_generatedInfo != null ? info.m_generatedInfo.m_size.y : 10f;
                                    heights += size * 0.5f * (info.m_minScale + info.m_maxScale);
                                }
                            }

                            id = tree.m_nextGridTree;
                        }
                    }

                    float ground = terrain != null ? terrain.SampleRawHeightSmoothWithWater(centre, false, 0f) : _mapFocusY;
                    float dustiness = count >= WoodTrees ? 0f : Dustiness(GroundUnder.At(centre)) * (1f - count / WoodTrees);
                    if (dustiness > 0.3f)
                        dusty++;
                    total += count;
                    _pixels[z * MapCells + x] = new Color(Mathf.Clamp01(count / WoodTrees), count > 0 ? heights / count : 0f, ground - _mapFocusY, dustiness);
                }
            }

            _trees = total;
            _dustyCells = dusty;
            _map.SetPixels(_pixels);
            _map.Apply(false);
        }

        /// <summary>How much dust a kind of ground gives up in a strong wind, 0..1.</summary>
        private static float Dustiness(GroundKind kind)
        {
            switch (kind)
            {
                case GroundKind.Water: return 0f;
                case GroundKind.Forest: return 0.15f;
                case GroundKind.City: return 0.35f;
                case GroundKind.Field: return 0.8f;
                default: return 1f; // dust, sand, snow
            }
        }

        /// <summary>The point the game's camera orbits (CameraController.m_currentPosition), else 300 m ahead.</summary>
        private Vector3 Focus()
        {
            if (_controller == null && !_lookedForController)
            {
                _lookedForController = true;
                _controller = _camera.GetComponent<CameraController>();
                if (_controller == null)
                    _controller = FindObjectOfType<CameraController>();
            }

            if (_controller != null)
                return _controller.m_currentPosition;
            return _camera.transform.position + _camera.transform.forward * 300f;
        }

        private static Vector4 AsVector(Color c)
        {
            return new Vector4(c.r, c.g, c.b, 0f);
        }

        /// <summary>Seeds, not positions (as FunnelDebris's flecks): POSITION and TEXCOORD1 random 0..1, TEXCOORD0 the corner.</summary>
        private static Mesh BuildSeeds(int count, int seed, string name)
        {
            System.Random random = new System.Random(seed);
            Vector3[] vertices = new Vector3[count * 4];
            Vector2[] corners = new Vector2[count * 4];
            List<Vector3> more = new List<Vector3>(count * 4);
            int[] triangles = new int[count * 6];

            for (int i = 0; i < count; i++)
            {
                Vector3 s = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                Vector3 extra = new Vector3((float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble());
                int v = i * 4;
                for (int c = 0; c < 4; c++)
                {
                    vertices[v + c] = s;
                    more.Add(extra);
                }

                corners[v + 0] = new Vector2(-1f, -1f);
                corners[v + 1] = new Vector2(1f, -1f);
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

            Mesh mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.uv = corners;
            mesh.SetUVs(1, more);
            mesh.triangles = triangles;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            return mesh;
        }

        private void OnDestroy()
        {
            if (_leaves != null)
                Destroy(_leaves);
            if (_dust != null)
                Destroy(_dust);
            if (_leafMesh != null)
                Destroy(_leafMesh);
            if (_dustMesh != null)
                Destroy(_dustMesh);
            if (_map != null)
                Destroy(_map);
        }
    }
}
