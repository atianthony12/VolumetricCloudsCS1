using System;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// "Trees move in the wind": the game's two tree shaders rebuilt (Trees*.shader: the same programs,
    /// checked against the game's by tools/tree-bench.ps1) put on the game's own tree materials, and
    /// the wind (<see cref="TreeWind"/>) published to them once a frame; the game's shaders given back
    /// when the row is off, the weather is hidden, an info view is open (ours carry the normal view
    /// only), or the city closes. Three kinds of material:
    /// - TreeInfo.m_material (Custom/Trees/Default): the near trees, one Graphics.DrawMesh each;
    /// - TreeInfo.m_lodMaterial (Custom/Trees/Group, MULTI_INSTANCE): the mid trees' billboards;
    /// - the render groups' tree layer, RenderManager.m_groupLayerMaterials[TreeManager.m_treeLayer]
    ///   (Custom/Trees/Group): the far trees' billboards.
    /// A material on any other shader (an asset's or another mod's own) is left as it is, said once.
    /// Each tree's own wind is the game's (WeatherManager.GetWindSpeed into _Color.a, _TreeColor[i].a
    /// and the far mesh's colour), so Tree Control's "sway factor" and "disable distant sway", which
    /// scale exactly that, still hold (a tree it stills stands still).
    /// </summary>
    public class TreeShaders : MonoBehaviour
    {
        private const string GameDefault = "Custom/Trees/Default";
        private const string GameGroup = "Custom/Trees/Group";
        private const float ScanInterval = 2f;
        private const float LogInterval = 5f;

        private static readonly int IdWind = Shader.PropertyToID("_VWTreeWind");
        private static readonly int IdWindDir = Shader.PropertyToID("_VWTreeWindDir");
        private static readonly int IdGust = Shader.PropertyToID("_VWTreeGust");
        private static readonly int IdClock = Shader.PropertyToID("_VWTreeClock");
        private static readonly int IdSize = Shader.PropertyToID("_VWTreeSize");
        private static readonly int IdGameWind = Shader.PropertyToID("_WindDirection");

        // The trees' share of the city's wind (WeatherWind: its direction, gusts and clock are theirs),
        // advanced by CloudLighting (the one place the weather moves) and read here.
        private static TreeWind.Wind _wind;
        private static bool _started;

        /// <summary>True while our tree shaders carry the wind (WindDebris draws only then).</summary>
        public static bool Active { get; private set; }

        /// <summary>The trees' wind as drawn now (eased), its clock and its direction: what the flying leaves and dust follow.</summary>
        public static TreeWind.Wind Current
        {
            get { return _wind; }
        }

        public static TreeWind.Motion Clock
        {
            get { return WeatherWind.Motion; }
        }

        public static Vector3 Blowing
        {
            get { return WeatherWind.Direction; }
        }

        /// <summary>Material -> the game's shader it had.</summary>
        private readonly Dictionary<Material, Shader> _held = new Dictionary<Material, Shader>();
        private readonly HashSet<Material> _skipped = new HashSet<Material>();

        private Shader _ours, _oursGroup;
        private bool _resolved, _on, _logged;
        private float _nextScan, _nextLog;
        private int _near, _mid, _far;
        private string _lastLine;

        /// <summary>
        /// Once a frame from CloudLighting, after WeatherWind: the trees' share of the city's wind -- the
        /// Trees rows scale how strongly they answer it (simulation time: a paused city's trees stand still).
        /// </summary>
        public static void Advance(float deltaTime, float simulationRate)
        {
            // Off means nothing runs (invariant 10).
            if (Settings.TreeWind == null || !Settings.TreeWind.value)
                return;
            TreeWind.Wind target = TreeWind.Compute(Value(Settings.TreeWindStrength, Settings.Defaults.TreeWindStrength),
                                                    Value(Settings.TreeGusts, Settings.Defaults.TreeGusts),
                                                    Value(Settings.TreeFlutter, Settings.Defaults.TreeFlutter),
                                                    Value(Settings.TreeStorms, Settings.Defaults.TreeStorms),
                                                    CloudWeather.Coverage, CloudWeather.Rain, WeatherWind.Storm,
                                                    WeatherWind.Overridden ? Value(Settings.WindStrength, Settings.Defaults.WindStrength) : -1f);

            // The trees follow a change of wind over a few seconds of simulation (paused: they hold).
            float seconds = deltaTime * simulationRate;
            if (!_started)
            {
                _started = true;
                _wind = target;
            }
            else
            {
                _wind = TreeWind.Ease(_wind, target, 1f - Mathf.Exp(-seconds / TreeWind.EaseSeconds));
            }
        }

        private static float Value(FloatSetting s, float fallback)
        {
            return s != null ? s.value : fallback;
        }

        /// <summary>Wanted: the row ticked, the weather shown, the game's normal view.</summary>
        private static bool Wanted()
        {
            if (Settings.TreeWind == null || !Settings.TreeWind.value || !Settings.WeatherShown)
                return false;
            return !Singleton<InfoManager>.exists || Singleton<InfoManager>.instance.CurrentMode == InfoManager.InfoMode.None;
        }

        private void Start()
        {
            if (Singleton<InfoManager>.exists)
                Singleton<InfoManager>.instance.EventInfoModeChanged += OnInfoModeChanged;
        }

        // An info view switches the shaders' keywords right away (InfoManager.SetActualMode): the game's
        // shaders back in the same frame, not one late.
        private void OnInfoModeChanged(InfoManager.InfoMode mode, InfoManager.SubInfoMode subMode)
        {
            if (mode != InfoManager.InfoMode.None && _on)
                GiveBack("an info view is open");
        }

        private void LateUpdate()
        {
            try
            {
                bool wanted = Wanted() && Resolve();
                if (!wanted)
                {
                    if (_on)
                        GiveBack(Settings.TreeWind != null && Settings.TreeWind.value ? (Settings.WeatherShown ? "an info view is open" : "the weather is hidden") : "the row is off");
                    return;
                }

                if (!_on || Time.time >= _nextScan)
                {
                    _nextScan = Time.time + ScanInterval;
                    Hold();
                }

                Publish();
                Report();
            }
            catch (Exception e)
            {
                Log.Error("trees: the wind failed; the game's tree shaders are given back", e);
                GiveBack("an error");
                enabled = false;
            }
        }

        private bool Resolve()
        {
            if (!_resolved)
            {
                _resolved = true;
                _ours = ShaderBundle.Get("VolumetricClouds/TreesDefault");
                _oursGroup = ShaderBundle.Get("VolumetricClouds/TreesGroup");
                if (_ours == null || _oursGroup == null)
                    Log.Msg("trees: our tree shaders are not available here; the trees keep the game's sway");
            }

            return _ours != null && _oursGroup != null;
        }

        /// <summary>Our shaders on every tree material still on the game's (new ones found every 2 s).</summary>
        private void Hold()
        {
            int near = 0, mid = 0, far = 0;
            uint count = (uint)PrefabCollection<TreeInfo>.LoadedCount();
            for (uint i = 0; i < count; i++)
            {
                TreeInfo info = PrefabCollection<TreeInfo>.GetLoaded(i);
                if (info == null)
                    continue;
                if (Take(info.m_material, GameDefault, _ours))
                {
                    near++;
                    // The tree's size for the sway's rate: its billboard's (twice the larger half-extent of
                    // its four corners), the same number the billboards work out, so they sway alike.
                    info.m_material.SetFloat(IdSize, 2f * BillboardHalf(info));
                }

                if (Take(info.m_lodMaterial, GameGroup, _oursGroup))
                    mid++;
            }

            if (Singleton<RenderManager>.exists && Singleton<TreeManager>.exists)
            {
                Material[] layers = Singleton<RenderManager>.instance.m_groupLayerMaterials;
                int layer = Singleton<TreeManager>.instance.m_treeLayer;
                if (layers != null && layer >= 0 && layer < layers.Length && Take(layers[layer], GameGroup, _oursGroup))
                    far++;
            }

            bool first = !_on;
            _on = true;
            if (first || near != _near || mid != _mid || far != _far)
            {
                _near = near;
                _mid = mid;
                _far = far;
                Log.Msg("trees: VW's wind on " + near + " near-tree and " + mid + " billboard materials" + (far > 0 ? " and the far trees' layer" : "") +
                        " (the game's shaders " + GameDefault + " / " + GameGroup + " rebuilt)" +
                        (_skipped.Count > 0 ? "; " + _skipped.Count + " tree material(s) on other shaders left as they are" : "") +
                        "; each tree's own wind is the game's, Tree Control's sway factors in it");
            }
        }

        private static float BillboardHalf(TreeInfo info)
        {
            float h = 0f;
            h = Mathf.Max(h, Mathf.Max(Mathf.Abs(info.m_renderUv0B.x), Mathf.Abs(info.m_renderUv0B.y)));
            h = Mathf.Max(h, Mathf.Max(Mathf.Abs(info.m_renderUv1B.x), Mathf.Abs(info.m_renderUv1B.y)));
            h = Mathf.Max(h, Mathf.Max(Mathf.Abs(info.m_renderUv2B.x), Mathf.Abs(info.m_renderUv2B.y)));
            h = Mathf.Max(h, Mathf.Max(Mathf.Abs(info.m_renderUv3B.x), Mathf.Abs(info.m_renderUv3B.y)));
            return h > 0.01f ? h : Mathf.Max(info.m_generatedInfo != null ? info.m_generatedInfo.m_size.y * 0.5f : 5f, 0.5f);
        }

        /// <summary>Ours on <paramref name="m"/> if it is on the game's <paramref name="game"/> (or already ours); true if ours is on it.</summary>
        private bool Take(Material m, string game, Shader ours)
        {
            if (m == null)
                return false;
            if (m.shader == ours)
                return true;
            if (_skipped.Contains(m))
                return false;
            Shader theirs = m.shader;
            if (theirs == null || theirs.name != game)
            {
                _skipped.Add(m);
                return false;
            }

            _held[m] = theirs;
            m.shader = ours;
            return true;
        }

        private void Publish()
        {
            Active = true;
            Vector3 d = WeatherWind.Direction;
            TreeWind.Globals g = TreeWind.GlobalsOf(_wind, WeatherWind.Motion, d.x, d.z);
            Shader.SetGlobalVector(IdWind, new Vector4(1f, g.Lean, g.Gusts, g.Flutter));
            Shader.SetGlobalVector(IdWindDir, new Vector4(g.DirX, 0f, g.DirZ, g.Clock));
            Shader.SetGlobalVector(IdGust, new Vector4(g.GustX, g.GustZ, g.InvCell, g.SwayShare));
            Shader.SetGlobalVector(IdClock, new Vector4(g.Period, g.Across, g.Branch, 0f));
        }

        private void Report()
        {
            float strength = Value(Settings.TreeWindStrength, Settings.Defaults.TreeWindStrength);
            string line = TreeWind.Line(_wind, strength, WeatherWind.Angle);
            string kind = TreeWind.Describe(_wind.Speed);
            if (!_logged || kind != _lastLine)
            {
                _logged = true;
                _lastLine = kind;
                Vector4 game = Shader.GetGlobalVector(IdGameWind);
                Log.Msg("trees: wind " + line + " (the game's own sway strength " + game.w.ToString("F2") + ", not used while ours is on)");
            }
            else if (Log.Detailed && Time.time >= _nextLog)
            {
                _nextLog = Time.time + LogInterval;
                Log.Detail("trees: wind " + line);
            }
        }

        private void GiveBack(string why)
        {
            int n = 0;
            foreach (KeyValuePair<Material, Shader> h in _held)
            {
                if (h.Key != null && (h.Key.shader == _ours || h.Key.shader == _oursGroup))
                {
                    h.Key.shader = h.Value;
                    n++;
                }
            }

            _held.Clear();
            _skipped.Clear();
            Shader.SetGlobalVector(IdWind, Vector4.zero);
            if (_on)
                Log.Msg("trees: the game's tree shaders given back on " + n + " material(s) (" + why + ")");
            _on = false;
            _logged = false;
            _started = false;
            Active = false;
            _near = _mid = _far = 0;
        }

        private void OnDestroy()
        {
            if (Singleton<InfoManager>.exists)
                Singleton<InfoManager>.instance.EventInfoModeChanged -= OnInfoModeChanged;
            GiveBack("the city closes");
            WeatherWind.Reset();
        }
    }
}
