using System;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// OTHER MODS' LIGHTS IN OUR AIR (1.6.2). Light Maker (its custom lights and warning lights) asked for its
    /// lights to be dimmed by our clouds, rain curtains and fog between them and the camera, and to light our fog.
    /// A light drawn over the picture after our pass knows nothing of the air in front of it; one drawn before
    /// it is covered by the air to the scene BEHIND it (a mast's light by the cloud kilometres behind the mast).
    /// So a mod hands in its lights' positions each frame (<see cref="Submit"/>), and once a frame, just before
    /// the game's camera renders, a pass marches from the eye to each of them through the same density the
    /// clouds, rain and fog are drawn with (LightAirPass.cginc) and writes how much of each light gets through.
    /// </summary>
    /// <remarks>
    /// FOR OTHER MODS (read by reflection; these names and meanings stay, <see cref="ApiVersion"/> says which;
    /// a new meaning is a new member and ApiVersion 2, readers check &gt;= 1):
    /// - <see cref="Active"/>: true while the pass runs (a city, our clouds on, its shader here). Read it each
    ///   frame; while false hand nothing in and draw as without us.
    /// - <see cref="Submit"/>(Vector4[] points, int count): this frame's points (xyz world), in the caller's
    ///   order; returns the index of the first (each mod gets its own run of the texture; the order of the
    ///   calls may change between frames, so read the index every frame), or -1 (off, or the texture full).
    /// - The globals: _VWLightAir (Side x Side, r = the share of the light that gets through, 1 = all; point
    ///   filtered, row by row from the bottom-left texel), _VWLightAirInfo (x = 1 when made this frame, else 0
    ///   and the texture not to be read; y = Side; z = 1 / Side; w = how many points it holds). Read in the
    ///   vertex shader: index i is texel (i mod Side, floor(i / Side)).
    /// - <see cref="AddFogLight"/>(pos, dir, colour, intensity, range, spotAngle): a light that lights our fog
    ///   this frame, as the game's lamps light it (LightSystem.DrawLight's units: a street lamp ~3-4; spotAngle
    ///   full degrees, 0 = all round). Lights the mod draws through LightSystem.DrawLight reach the fog already
    ///   (HaloPatches): only the ones it draws itself need this.
    /// Nothing of it runs, costs or changes anything for a player without such a mod: no points, no pass.
    /// </remarks>
    public static class LightAir
    {
        public const int ApiVersion = 1;

        /// <summary>Texels each way: Side x Side points at most (4096).</summary>
        public const int Side = 64;
        public const int Capacity = Side * Side;

        private static readonly int IdLightAir = Shader.PropertyToID("_VWLightAir");
        private static readonly int IdLightAirInfo = Shader.PropertyToID("_VWLightAirInfo");
        private static readonly int IdPoints = Shader.PropertyToID("_LightAirPoints");
        private static readonly int IdInfo = Shader.PropertyToID("_LightAirInfo");
        private static readonly int IdEye = Shader.PropertyToID("_LightAirEye");

        private static readonly Color[] Points = new Color[Capacity];
        private static int _used;
        private static int _submitFrame = -1;
        private static float _lastSubmit = -100f;
        private static LightAirPass _pass;

        /// <summary>The pass is there and runs when given points (see the remarks).</summary>
        public static bool Active
        {
            get { return _pass != null && !_pass.Failed && CloudVolume.IsActive; }
        }

        /// <summary>A mod handed points in within the last two seconds: CloudVolume keeps the pass's values up to date.</summary>
        internal static bool Wanted
        {
            get { return _pass != null && Time.time - _lastSubmit < 2f; }
        }

        /// <summary>This frame's points of one mod; the index of its first in the texture, or -1.</summary>
        public static int Submit(Vector4[] points, int count)
        {
            if (!Active || points == null || count <= 0)
                return -1;

            int frame = Time.frameCount;
            if (frame != _submitFrame)
            {
                _submitFrame = frame;
                _used = 0;
            }

            int first = _used;
            int take = Mathf.Min(Mathf.Min(count, points.Length), Capacity - first);
            if (take <= 0)
                return -1;

            for (int i = 0; i < take; i++)
            {
                Vector4 p = points[i];
                Points[first + i] = new Color(p.x, p.y, p.z, 1f);
            }

            _used += take;
            _lastSubmit = Time.time;
            return first;
        }

        /// <summary>A light of another mod's that lights our fog this frame (FogLampMap, as the game's dynamic lights).</summary>
        public static void AddFogLight(Vector3 pos, Vector3 dir, Color color, float intensity, float range, float spotAngle)
        {
            if (!FogLampMap.Collecting || intensity <= 0f || range <= 0f)
                return;

            bool spot = spotAngle > 0.01f && spotAngle < 179.9f;
            FogLampMap.Record(spot ? LightType.Spot : LightType.Point, pos, dir, color, intensity, range, spot ? spotAngle : 0f);
        }

        // ---- the pass (CloudVolume owns it) ---------------------------------------------------

        internal static LightAirPass TryCreate()
        {
            Shader shader = ShaderBundle.Get(CloudShaders.Lite ? ShaderBundle.LightAirLite : ShaderBundle.LightAir);
            if (shader == null || !shader.isSupported)
            {
                Log.Msg("lights' air: " + (shader == null ? "no shader for it in this bundle" : "its shader is not supported here") +
                        "; other mods' lights are drawn as without us");
                return null;
            }

            _pass = new LightAirPass(shader);
            return _pass;
        }

        internal static void Forget(LightAirPass pass)
        {
            if (_pass == pass)
                _pass = null;
            _used = 0;
            _submitFrame = -1;
            Shader.SetGlobalVector(IdLightAirInfo, Vector4.zero);
        }

        internal sealed class LightAirPass
        {
            private readonly Shader _shader;
            private Material _material;
            private Texture2D _points;
            private RenderTexture _target;
            private int _drawnFrame = -1;
            private bool _prepared;
            private bool _loggedFirst;

            public bool Failed { get; private set; }

            public LightAirPass(Shader shader)
            {
                _shader = shader;
                Camera.onPreCull += OnPreCull;
            }

            /// <summary>
            /// Once a frame from CloudVolume, after it set the cloud and air materials: their values into ours
            /// (the textures one by one: Unity 5.6 copies only those in a Properties block), while points come in.
            /// </summary>
            public void Prepare(Material from, CloudDensityField field, Texture3D noise)
            {
                if (Failed || !Wanted || from == null)
                    return;

                if (_material == null)
                    _material = new Material(_shader) { name = "VolumetricCloudsLightAir" };

                CloudVolume.CopyValuesInto(from, _material);
                CloudShaderParams.Apply(_material, field, noise);
                Texture terrain = TerrainHeightMap.Texture;
                if (terrain != null)
                    _material.SetTexture("_TerrainTex", terrain);
                Texture places = TerrainHeightMap.Places;
                if (places != null)
                    _material.SetTexture("_FogPlaceTex", places);
                if (CloudShaders.PlusOn)
                    _material.EnableKeyword(CloudShaders.PlusKeyword);
                else
                    _material.DisableKeyword(CloudShaders.PlusKeyword);
                _prepared = true;
            }

            /// <summary>Before the game's camera culls (after every LateUpdate, when the points are in): the pass, once a frame.</summary>
            private void OnPreCull(Camera camera)
            {
                if (Failed || camera == null || camera != GameCamera.Get())
                    return;

                int frame = Time.frameCount;
                if (frame == _drawnFrame)
                    return; // A second render this frame (Dynamic Resolution, say): the same eye, the same answer.
                _drawnFrame = frame;

                if (_submitFrame != frame || _used <= 0 || !_prepared || !CloudVolume.IsActive)
                {
                    Shader.SetGlobalVector(IdLightAirInfo, Vector4.zero);
                    return;
                }

                try
                {
                    if (!Ensure())
                        return;

                    int rows = (_used + Side - 1) / Side;
                    _points.SetPixels(0, 0, Side, rows, RowsOf(rows));
                    _points.Apply(false);

                    _material.SetTexture(IdPoints, _points);
                    _material.SetVector(IdInfo, new Vector4(_used, Side, 1f / Side, 0f));
                    Vector3 eye = camera.transform.position;
                    _material.SetVector(IdEye, new Vector4(eye.x, eye.y, eye.z, 0f));

                    RenderTexture active = RenderTexture.active;
                    Graphics.Blit(null, _target, _material);
                    RenderTexture.active = active;

                    Shader.SetGlobalTexture(IdLightAir, _target);
                    Shader.SetGlobalVector(IdLightAirInfo, new Vector4(1f, Side, 1f / Side, _used));

                    if (!_loggedFirst)
                    {
                        _loggedFirst = true;
                        Log.Msg("lights' air: another mod's lights dimmed by our clouds, rain and fog in front of them (" + _used +
                                " points this frame, " + (CloudShaders.Lite ? "Simpler clouds" : "the whole clouds") + ")");
                    }
                }
                catch (Exception e)
                {
                    Failed = true;
                    Shader.SetGlobalVector(IdLightAirInfo, Vector4.zero);
                    Log.Warn("lights' air: stopped (" + e.Message + "); other mods' lights are drawn as without us");
                }
            }

            private Color[] _rows;

            private Color[] RowsOf(int rows)
            {
                int n = rows * Side;
                if (_rows == null || _rows.Length != n)
                    _rows = new Color[n];
                Array.Copy(Points, _rows, Mathf.Min(_used, n));
                for (int i = _used; i < n; i++)
                    _rows[i] = new Color(0f, 0f, 0f, 0f);
                return _rows;
            }

            private bool Ensure()
            {
                if (_points == null)
                {
                    // World positions need full floats (a half is metres off at the map's edge): none here, no pass.
                    if (!SystemInfo.SupportsTextureFormat(TextureFormat.RGBAFloat))
                    {
                        Failed = true;
                        Log.Msg("lights' air: this GPU has no float textures for the lights' positions; other mods' lights are drawn as without us");
                        return false;
                    }

                    _points = new Texture2D(Side, Side, TextureFormat.RGBAFloat, false, true)
                    {
                        name = "VolumetricCloudsLightAirPoints",
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp,
                    };
                }

                if (_target != null && !_target.IsCreated())
                {
                    _target.Release();
                    UnityEngine.Object.Destroy(_target);
                    _target = null;
                }

                if (_target == null)
                {
                    _target = new RenderTexture(Side, Side, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
                    {
                        name = "VolumetricCloudsLightAir",
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp,
                        useMipMap = false,
                    };
                    if (!_target.Create())
                    {
                        Failed = true;
                        UnityEngine.Object.Destroy(_target);
                        _target = null;
                        Log.Msg("lights' air: its " + Side + "x" + Side + " target could not be made; other mods' lights are drawn as without us");
                        return false;
                    }
                }

                return true;
            }

            public void Dispose()
            {
                Camera.onPreCull -= OnPreCull;
                Forget(this);
                if (_material != null)
                    UnityEngine.Object.Destroy(_material);
                if (_points != null)
                    UnityEngine.Object.Destroy(_points);
                if (_target != null)
                {
                    _target.Release();
                    UnityEngine.Object.Destroy(_target);
                }

                _material = null;
                _points = null;
                _target = null;
            }
        }
    }
}
