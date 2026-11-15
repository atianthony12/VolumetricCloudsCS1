using System;
using ColossalFramework;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// What keeps the rain off (2026-11-15, the author after Torrent: "rain stops ... under roofs, bridges"):
    /// a top-down map, 512 m round the camera at a metre a texel, of the highest cover over each spot --
    /// buildings as their mesh's footprint and top, bridges and elevated roads as strips at their height.
    /// RainDrops.shader and RainSplash.shader hide a drop below the cover over the place it fell from
    /// (traced back up its slanted path, so a wind blows rain in under an overhang's edge).
    /// </summary>
    /// <remarks>
    /// Built on the CPU from the game's own grids (BuildingManager / NetManager, 64 m cells), on the main
    /// thread as the game's renderer reads them (guarded chains), when the camera has moved 64 m or every
    /// 3 s (a new building). About 300 buildings: well under 2 ms, every few seconds. An approximation:
    /// a building is its bounding box (an open-sided canopy keeps all rain off, a roof's eaves are the box's
    /// edge), props, trees and overhangs out of a building's box are not cover.
    ///
    /// Part of "Finer rain": nothing exists while it is off or it does not rain (invariant 10).
    /// Point-filtered RFloat; "no cover" = -100000 m.
    /// </remarks>
    public sealed class RainCover
    {
        public const int Resolution = 512;
        public const float Size = 512f;
        private const float NoCover = -100000f;
        private const float RebuildSeconds = 3f;
        private const float RebuildMove = 64f;

        private static readonly int IdCover = Shader.PropertyToID("_RainCover");
        private static readonly int IdCoverRect = Shader.PropertyToID("_RainCoverRect");

        private Texture2D _texture;
        private float[] _heights;
        private byte[] _bytes;
        private Vector2 _corner;
        private Vector3 _builtAt;
        private float _nextBuild;
        private int _buildings, _segments;
        private bool _failed;
        private bool _logged;

        /// <summary>
        /// Once a frame while Finer rain draws: rebuilds when due, and sets the map on the materials.
        /// </summary>
        public void Apply(Vector3 eye, Material a, Material b)
        {
            if (_failed)
            {
                Off(a, b);
                return;
            }

            if (_texture == null && !Create())
            {
                Off(a, b);
                return;
            }

            if (Time.time >= _nextBuild || (eye - _builtAt).sqrMagnitude > RebuildMove * RebuildMove)
            {
                _nextBuild = Time.time + RebuildSeconds;
                try
                {
                    Build(eye);
                }
                catch (Exception e)
                {
                    _failed = true;
                    Log.Error("rain: reading the buildings and roads for the cover threw; rain falls everywhere for this city", e);
                    Off(a, b);
                    return;
                }
            }

            Vector4 rect = new Vector4(_corner.x, _corner.y, 1f / Size, 1f);
            Set(a, rect);
            Set(b, rect);
        }

        private void Set(Material m, Vector4 rect)
        {
            if (m == null)
                return;
            m.SetTexture(IdCover, _texture);
            m.SetVector(IdCoverRect, rect);
        }

        public static void Off(Material a, Material b)
        {
            if (a != null) a.SetVector(IdCoverRect, Vector4.zero);
            if (b != null) b.SetVector(IdCoverRect, Vector4.zero);
        }

        private bool Create()
        {
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.RFloat))
            {
                _failed = true;
                Log.Msg("rain: no single-float textures here; rain is not kept from under roofs");
                return false;
            }

            _texture = new Texture2D(Resolution, Resolution, TextureFormat.RFloat, false, true)
            {
                name = "VolumetricCloudsRainCover",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                anisoLevel = 0,
            };
            _heights = new float[Resolution * Resolution];
            _bytes = new byte[Resolution * Resolution * 4];
            _builtAt = new Vector3(float.MaxValue, 0f, 0f);
            return true;
        }

        public void Release()
        {
            if (_texture != null)
                UnityEngine.Object.Destroy(_texture);
            _texture = null;
            _heights = null;
            _bytes = null;
            _nextBuild = 0f;
        }

        private void Build(Vector3 eye)
        {
            // The map's corner on whole 16 m steps, so a building's edge does not shimmer as the camera moves.
            _corner = new Vector2(Mathf.Floor((eye.x - Size * 0.5f) / 16f) * 16f, Mathf.Floor((eye.z - Size * 0.5f) / 16f) * 16f);
            _builtAt = eye;
            for (int i = 0; i < _heights.Length; i++)
                _heights[i] = NoCover;

            _buildings = Singleton<BuildingManager>.exists ? Buildings() : 0;
            _segments = Singleton<NetManager>.exists ? Segments() : 0;

            Buffer.BlockCopy(_heights, 0, _bytes, 0, _bytes.Length);
            _texture.LoadRawTextureData(_bytes);
            _texture.Apply(false);

            if (!_logged)
            {
                _logged = true;
                Log.Msg("rain: kept from under cover -- " + _buildings + " buildings and " + _segments + " bridge or elevated road pieces in the " +
                        Size.ToString("F0") + " m round the camera (rebuilt every " + RebuildSeconds.ToString("F0") + " s or " +
                        RebuildMove.ToString("F0") + " m)");
            }
            else if (Log.Detailed)
            {
                Log.Detail("rain cover: " + _buildings + " buildings, " + _segments + " elevated road pieces");
            }
        }

        private int Buildings()
        {
            BuildingManager manager = Singleton<BuildingManager>.instance;
            Building[] buffer = manager.m_buildings.m_buffer;
            ushort[] grid = manager.m_buildingGrid;
            const int Res = 270;
            const float Cell = 64f;
            int x0 = Mathf.Clamp((int)(_corner.x / Cell + Res / 2f) - 1, 0, Res - 1);
            int z0 = Mathf.Clamp((int)(_corner.y / Cell + Res / 2f) - 1, 0, Res - 1);
            int x1 = Mathf.Clamp((int)((_corner.x + Size) / Cell + Res / 2f) + 1, 0, Res - 1);
            int z1 = Mathf.Clamp((int)((_corner.y + Size) / Cell + Res / 2f) + 1, 0, Res - 1);
            int count = 0;

            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    ushort id = grid[z * Res + x];
                    int guard = 0;
                    while (id != 0 && guard++ < 49152)
                    {
                        Building.Flags flags = buffer[id].m_flags;
                        if ((flags & (Building.Flags.Created | Building.Flags.Deleted | Building.Flags.Hidden)) == Building.Flags.Created)
                        {
                            BuildingInfo info = buffer[id].Info;
                            BuildingInfoGen gen = info != null ? info.m_generatedInfo : null;
                            if (gen != null && gen.m_max.y > 2f)
                            {
                                Box(buffer[id].m_position, buffer[id].m_angle, gen.m_min, gen.m_max);
                                count++;
                            }
                        }
                        id = buffer[id].m_nextGridBuilding;
                    }
                }
            }

            return count;
        }

        /// <summary>A building's box: local footprint min..max (x, z), turned by its angle, its top max.y over its place.</summary>
        private void Box(Vector3 position, float angle, Vector3 min, Vector3 max)
        {
            // The game turns a building by AngleAxis(angle, down): local (x, z) -> world as below (CoverRules twin).
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            float top = position.y + max.y;

            // The footprint's corners, for its bounds on the map.
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int k = 0; k < 4; k++)
            {
                float lx = (k & 1) == 0 ? min.x : max.x;
                float lz = (k & 2) == 0 ? min.z : max.z;
                float wx = position.x + lx * c - lz * s;
                float wz = position.z + lx * s + lz * c;
                minX = Mathf.Min(minX, wx); maxX = Mathf.Max(maxX, wx);
                minZ = Mathf.Min(minZ, wz); maxZ = Mathf.Max(maxZ, wz);
            }

            int tx0 = Mathf.Max(0, (int)(minX - _corner.x)), tx1 = Mathf.Min(Resolution - 1, (int)(maxX - _corner.x));
            int tz0 = Mathf.Max(0, (int)(minZ - _corner.y)), tz1 = Mathf.Min(Resolution - 1, (int)(maxZ - _corner.y));
            for (int tz = tz0; tz <= tz1; tz++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    // Back into the building's own frame (the inverse turn).
                    float dx = _corner.x + tx + 0.5f - position.x, dz = _corner.y + tz + 0.5f - position.z;
                    float lx = dx * c + dz * s;
                    float lz = -dx * s + dz * c;
                    if (lx < min.x || lx > max.x || lz < min.z || lz > max.z)
                        continue;
                    int i = tz * Resolution + tx;
                    if (top > _heights[i])
                        _heights[i] = top;
                }
            }
        }

        private int Segments()
        {
            NetManager manager = Singleton<NetManager>.instance;
            NetSegment[] segments = manager.m_segments.m_buffer;
            NetNode[] nodes = manager.m_nodes.m_buffer;
            ushort[] grid = manager.m_segmentGrid;
            TerrainManager terrain = Singleton<TerrainManager>.exists ? Singleton<TerrainManager>.instance : null;
            const int Res = 270;
            const float Cell = 64f;
            int x0 = Mathf.Clamp((int)(_corner.x / Cell + Res / 2f) - 1, 0, Res - 1);
            int z0 = Mathf.Clamp((int)(_corner.y / Cell + Res / 2f) - 1, 0, Res - 1);
            int x1 = Mathf.Clamp((int)((_corner.x + Size) / Cell + Res / 2f) + 1, 0, Res - 1);
            int z1 = Mathf.Clamp((int)((_corner.y + Size) / Cell + Res / 2f) + 1, 0, Res - 1);
            int count = 0;

            for (int z = z0; z <= z1; z++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    ushort id = grid[z * Res + x];
                    int guard = 0;
                    while (id != 0 && guard++ < 36864)
                    {
                        NetSegment seg = segments[id];
                        if ((seg.m_flags & (NetSegment.Flags.Created | NetSegment.Flags.Deleted)) == NetSegment.Flags.Created)
                        {
                            NetInfo info = seg.Info;
                            Vector3 a = nodes[seg.m_startNode].m_position;
                            Vector3 b = nodes[seg.m_endNode].m_position;
                            Vector3 m = seg.m_middlePosition;
                            float ground = terrain != null ? terrain.SampleRawHeightSmooth(m) : 0f;
                            // Elevated: its middle stands 4 m or more over the ground (a bridge, a raised road,
                            // a viaduct); a tunnel or a road on the ground is no cover.
                            if (info != null && m.y > ground + 4f && a.y > ground - 2f)
                            {
                                float half = Mathf.Max(2f, info.m_halfWidth);
                                Strip(a, m, half);
                                Strip(m, b, half);
                                count++;
                            }
                        }
                        id = seg.m_nextGridSegment;
                    }
                }
            }

            return count;
        }

        /// <summary>A straight piece of road from a to b, half wide, its deck 0.5 m over the line.</summary>
        private void Strip(Vector3 a, Vector3 b, float half)
        {
            float minX = Mathf.Min(a.x, b.x) - half, maxX = Mathf.Max(a.x, b.x) + half;
            float minZ = Mathf.Min(a.z, b.z) - half, maxZ = Mathf.Max(a.z, b.z) + half;
            int tx0 = Mathf.Max(0, (int)(minX - _corner.x)), tx1 = Mathf.Min(Resolution - 1, (int)(maxX - _corner.x));
            int tz0 = Mathf.Max(0, (int)(minZ - _corner.y)), tz1 = Mathf.Min(Resolution - 1, (int)(maxZ - _corner.y));
            if (tx0 > tx1 || tz0 > tz1)
                return;

            float ex = b.x - a.x, ez = b.z - a.z;
            float lengthSq = Mathf.Max(ex * ex + ez * ez, 1e-4f);
            for (int tz = tz0; tz <= tz1; tz++)
            {
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    float px = _corner.x + tx + 0.5f - a.x, pz = _corner.y + tz + 0.5f - a.z;
                    float t = Mathf.Clamp01((px * ex + pz * ez) / lengthSq);
                    float qx = px - ex * t, qz = pz - ez * t;
                    if (qx * qx + qz * qz > half * half)
                        continue;
                    float top = Mathf.Lerp(a.y, b.y, t) + 0.5f;
                    int i = tz * Resolution + tx;
                    if (top > _heights[i])
                        _heights[i] = top;
                }
            }
        }
    }
}
