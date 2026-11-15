using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// Lightning that belongs to the clouds: every strike lights the cloud layer and the rain
    /// from inside, and comes down as a bolt from OUR cloud base.
    /// </summary>
    /// <remarks>
    /// Where strikes come from (read from the IL; the scheduler in RainProperties is a red
    /// herring -- that component is disabled in a normal game):
    ///   WeatherManager.QueueLightningStrike  called by the weather's own simulation step in
    ///       heavy rain and by ThunderStormAI. Entries of {m_startFrame, m_position,
    ///       m_rotation} go into the private m_lightningQueue. StrikeNow plays the thunder
    ///       and sets fire to whatever was hit.
    ///   WeatherManager.EndRenderingImpl      draws each entry for 30 simulation frames: its
    ///       bolt MODEL, a small light at the base, and a 10 km-range light 500 m up.
    /// Those lights reach the city but not our clouds, which only know the sun, the moon and
    /// the ambient. So this reads the same queue and gives the cloud shader a point source
    /// inside the layer above each strike, flickering with the game's own formula
    /// (<see cref="LightningFlicker"/>) so cloud, ground and bolt pulse as one. The game's strikes are
    /// never created, moved or suppressed here: they start fires.
    ///
    /// On top of that comes purely visual lightning -- no fire, nothing the simulation can
    /// see -- whose rate follows the rain or the player's override. It only ever starts
    /// INSIDE cloud: candidates are tested against the same weather field the clouds are cut
    /// from, and a clear sky has none.
    ///
    /// The game's bolt model is hidden by swapping its public material for Invisible.shader;
    /// the mesh cannot simply be nulled, because the same block draws the ground lights.
    /// </remarks>
    public class CloudLightning : MonoBehaviour
    {
        public const int MaxFlashes = 4;

        private const uint StrikeFrames = LightningFlicker.StrikeFrames;
        private const float LogInterval = 5f;
        private const float SpeedOfSound = 343f;
        private const float MaxThunderDelay = 6f;
        /// <summary>With Lightning variety on: travelling storms reach further, and the delay tells the distance.</summary>
        private const float MaxThunderDelayVariety = 10f;

        private static readonly int IdBoltLeader = Shader.PropertyToID("_BoltLeader");

        private static readonly int IdFlashCount = Shader.PropertyToID("_FlashCount");
        private static readonly int IdFlashPos = Shader.PropertyToID("_FlashPos");
        private static readonly int IdFlashColor = Shader.PropertyToID("_FlashColor");
        private static readonly int IdBoltColor = Shader.PropertyToID("_BoltColor");
        private static readonly int IdBoltIntensity = Shader.PropertyToID("_BoltIntensity");
        private static readonly int IdPixelAngle = Shader.PropertyToID("_PixelAngle");

        /// <summary>Our bolt's queue is this much under the clouds': just before them (see <see cref="DrawBolts"/>).</summary>
        private const int BoltBeforeClouds = 10;

        /// <summary>The queue a bolt was last drawn at, read back, for the one log line per change.</summary>
        private int _boltQueueLogged = -1;

        // What the cloud pass reads. Always MaxFlashes long: Unity fixes an array's size on first use.
        private static readonly Vector4[] FlashPos = new Vector4[MaxFlashes];
        private static readonly Vector4[] FlashColor = new Vector4[MaxFlashes];
        private static int _flashCount;

        private sealed class Strike
        {
            public uint StartFrame;
            public Vector3 Ground;
            public Vector3 Source;
            public LightningFlicker.Profile Profile;
            public bool FromGame;
            public Mesh Bolt;
            public float ThunderIn = -1f;
            public float RollIn = -1f;      // a second, lower roll of thunder, for some strikes
            public float Intensity;
            public bool Seen;
            /// <summary>Its LightningBoltShape kind, -1 for a sheet or a 1.5.3 strike (variety 0).</summary>
            public int Kind = -1;
            /// <summary>Is a follow-up of a burst (never bursts again).</summary>
            public bool FollowUp;

            /// <summary>Sheet lightning is seen through more cloud than a stroke to the ground.</summary>
            public float Power
            {
                get { return Profile.Power * (Bolt != null || FromGame ? 1f : 0.8f); }
            }

            public bool Flashing(uint frame)
            {
                return frame >= StartFrame && frame < StartFrame + Profile.Frames;
            }
        }

        private readonly Dictionary<ulong, Strike> _gameStrikes = new Dictionary<ulong, Strike>();
        private readonly List<Strike> _ownStrikes = new List<Strike>();
        private readonly List<Strike> _active = new List<Strike>();
        private readonly List<ulong> _expired = new List<ulong>();

        private CloudDensityField _field;
        private Camera _camera;
        private FieldInfo _queueField;
        private bool _queueResolved;

        private Material _boltMaterial;
        private BoltMap _boltMap;
        private readonly List<Mesh> _drawnBolts = new List<Mesh>();
        private Material _invisible;
        private Material _gameBoltMaterial;
        private bool _gameBoltHidden;
        private MaterialPropertyBlock _block;

        private double _ownClock = 1000.0;
        private System.Random _random = new System.Random();
        private Randomizer _soundRandomizer = new Randomizer(1u);
        private float _reduction;
        private float _lastTotal;
        private float _nextLogTime;
        private int _gameSeen;
        private int _ownSpawned;
        private int _ownInView;
        private int _ownSkippedNoCloud;
        private bool _loggedCamera;

        // Travelling storms and bursts (2026-11-15).
        private readonly StormCells _cells = new StormCells();
        private readonly List<LightningBursts.FollowUp> _burst = new List<LightningBursts.FollowUp>();

        private struct Pending
        {
            public float In;
            public Vector3 Near;
            public bool Twin;
        }

        private readonly List<Pending> _pending = new List<Pending>();
        private int _ownFollowUps;
        private int _ownInCell;
        private int _loggedMovement = -1;
        private System.Func<Vector3, float> _cloudAt;

        /// <summary>"Lightning variety", 0..2 (1 = 100%).</summary>
        private static float Variety
        {
            get { return Settings.LightningVariety != null ? Mathf.Clamp(Settings.LightningVariety.value, 0f, 2f) : Settings.Defaults.LightningVariety; }
        }

        /// <summary>"Storms" is Travelling storms.</summary>
        private static bool Travelling
        {
            get { return Settings.StormMovement != null && Settings.StormMovement.value == 1; }
        }

        /// <summary>
        /// Seconds between thunderclaps, whatever the strike rate. At 400% more than one strike
        /// a second reaches the ear, and the game's AudioManager would play every one of them:
        /// a wall of noise rather than a storm. The nearest pending strike wins the slot and
        /// the rest keep only their roll, or stay silent.
        /// </summary>
        private const float ThunderCooldown = 1.2f;

        private float _thunderReady;

        public void Initialise(CloudDensityField field)
        {
            _field = field;
        }

        /// <summary>Sets the lightning uniforms of the cloud pass. Safe when nothing is running.</summary>
        public static void ApplyTo(Material material)
        {
            material.SetFloat(IdFlashCount, _flashCount);
            material.SetVectorArray(IdFlashPos, FlashPos);
            material.SetVectorArray(IdFlashColor, FlashColor);
        }

        private void Start()
        {
            _block = new MaterialPropertyBlock();

            Shader bolt = ShaderBundle.Get(ShaderBundle.LightningBolt);
            if (bolt != null)
                _boltMaterial = new Material(bolt) { name = "VolumetricCloudsLightningBolt" };
            _boltMap = new BoltMap();

            Shader invisible = ShaderBundle.Get(ShaderBundle.Invisible);
            if (invisible != null)
                _invisible = new Material(invisible) { name = "VolumetricCloudsInvisibleBolt" };

            Log.Msg("lightning: ready (bolt shader " + (_boltMaterial != null ? "ok" : "MISSING") + ")");
        }

        private void LateUpdate()
        {
            if (_field == null || !Singleton<WeatherManager>.exists || !Singleton<SimulationManager>.exists)
                return;

            if (_camera == null)
                _camera = GameCamera.Get();
            if (_camera == null)
                return;

            bool enabled = (Settings.LightningEnabled == null || Settings.LightningEnabled.value)
                        && Settings.WeatherShown;

            HideGameBolt(enabled && _boltMaterial != null && _invisible != null
                         && (Settings.LightningReplaceBolt == null || Settings.LightningReplaceBolt.value));

            if (!enabled)
            {
                Clear();
                return;
            }

            SimulationManager simulation = Singleton<SimulationManager>.instance;
            bool paused = simulation.SimulationPaused || simulation.ForcedSimulationPaused;
            uint frame = simulation.m_referenceFrameIndex;

            // Our own strikes run on their own 60 Hz clock. It stops with the game like
            // everything else.
            if (!paused)
                _ownClock += Time.deltaTime * 60.0;
            uint ownFrame = (uint)_ownClock;

            AdvanceCells(paused, simulation);

            ReadGameStrikes(frame);
            SpawnOwnStrikes(ownFrame, paused);
            Evaluate(frame, ownFrame, paused);
            DrawBolts();

            if (!_loggedCamera)
            {
                // The placement assumes a field of view of roughly 70 degrees across; this is
                // the game telling us what it really is.
                _loggedCamera = true;
                Log.Msg("lightning: camera fov=" + _camera.fieldOfView.ToString("F0") + "deg aspect=" +
                        _camera.aspect.ToString("F2") + "; " + (LightningPlacement.InViewShare * 100f).ToString("F0") +
                        "% of strikes are aimed through the viewport");
            }

            if (Time.time >= _nextLogTime)
            {
                _nextLogTime = Time.time + LogInterval;

                if (Log.Detailed)
                {
                    float activity = Activity();
                    Log.Detail("lightning: activity=" + activity.ToString("F2") +
                               (activity > 0f
                                   ? " (every " + LightningPlacement.SecondsBetween(activity).ToString("F1") + " s)"
                                   : " (none)") +
                               (Overridden ? " (override)" : " (from rain " + CloudWeather.Rain.ToString("F2") + ")") +
                               " | last 5s: game strikes=" + _gameSeen + " own=" + _ownSpawned +
                               " ofWhichInView=" + _ownInView +
                               " skippedNoCloud=" + _ownSkippedNoCloud +
                               " followUps=" + _ownFollowUps + " inCell=" + _ownInCell +
                               " | variety " + (Variety * 100f).ToString("F0") + "%, storms " +
                               (Travelling ? "travelling: " + _cells.Describe() + ", clouds drift " + _cells.DriftSpeed.ToString("F1") + " m/s" : "around the camera") +
                               " | active=" + _active.Count + " flashingReduction=" + _reduction.ToString("F2") +
                               " gameBolt=" + (_gameBoltHidden ? "hidden" : "shown") +
                               " | bolt map " + (_boltMap != null ? _boltMap.Describe() : "none"));
                }

                _gameSeen = 0;
                _ownSpawned = 0;
                _ownInView = 0;
                _ownSkippedNoCloud = 0;
                _ownFollowUps = 0;
                _ownInCell = 0;
            }
        }

        /// <summary>Where the camera looks: the point the game's camera orbits, else under the camera.</summary>
        private Vector3 LookAt()
        {
            CameraController controller = GameCamera.Controller;
            Vector3 point = controller != null ? controller.m_currentPosition : _camera.transform.position;
            if (Singleton<TerrainManager>.exists)
                point.y = Singleton<TerrainManager>.instance.SampleRawHeightSmooth(point);
            return point;
        }

        /// <summary>Travelling storms: the cells move with the clouds; off, they go.</summary>
        private void AdvanceCells(bool paused, SimulationManager simulation)
        {
            int movement = Travelling ? 1 : 0;
            if (movement != _loggedMovement)
            {
                _loggedMovement = movement;
                Log.Msg("lightning: storms " + (movement == 1
                    ? "travel -- each is born upwind of where the camera looks, passes over and moves away downwind"
                    : "stay around the camera (as before)") + "; variety " + (Variety * 100f).ToString("F0") + "%");
            }

            if (movement == 0)
            {
                _cells.Clear("storms around the camera");
                return;
            }

            float activity = Activity();
            if (activity <= 0f)
            {
                if (_cells.Cells.Count > 0)
                    _cells.Clear("no storm: the rain or the cover dropped");
                return;
            }

            if (_cloudAt == null)
                _cloudAt = CloudAt;

            float simSeconds = paused ? 0f : Time.deltaTime * SimulationRate(simulation);
            _cells.Advance(simSeconds, Time.deltaTime, LookAt(), CloudWeather.WindDirection, activity, _camera, _cloudAt, _random);
        }

        /// <summary>Simulation speed: 1, 2 or 3 (the wind and the clouds move with it).</summary>
        private static float SimulationRate(SimulationManager simulation)
        {
            return Mathf.Max(1, simulation.FinalSimulationSpeed);
        }

        // ---- the game's strikes -------------------------------------------------------------

        private void ReadGameStrikes(uint frame)
        {
            foreach (Strike strike in _gameStrikes.Values)
                strike.Seen = false;

            FastList<WeatherManager.LightningStrike> queue = Queue();
            if (queue != null)
            {
                try
                {
                    // The simulation thread appends to this list. Take the buffer first and
                    // never index past it, exactly as careful as the game's own renderer.
                    WeatherManager.LightningStrike[] buffer = queue.m_buffer;
                    int count = buffer == null ? 0 : Mathf.Min(queue.m_size, buffer.Length);

                    for (int i = 0; i < count; i++)
                    {
                        uint start = buffer[i].m_startFrame;
                        if (frame < start || frame >= start + StrikeFrames)
                            continue;

                        Vector3 ground = buffer[i].m_position;
                        ulong key = ((ulong)start << 32) ^ (uint)ground.GetHashCode();

                        Strike strike;
                        if (!_gameStrikes.TryGetValue(key, out strike))
                        {
                            strike = NewStrike(start, ground, true, true);
                            _gameStrikes.Add(key, strike);
                            _gameSeen++;

                            Log.Msg("lightning: game strike at (" + ground.x.ToString("F0") + ", " + ground.z.ToString("F0") +
                                    "), " + Vector3.Distance(ground, _camera.transform.position).ToString("F0") +
                                    " m away, cloud above=" + CloudAt(ground).ToString("F2"));
                        }

                        strike.Seen = true;
                    }
                }
                catch (Exception e)
                {
                    Log.Error("Reading the game's lightning queue threw.", e);
                }
            }

            _expired.Clear();
            foreach (KeyValuePair<ulong, Strike> pair in _gameStrikes)
            {
                if (!pair.Value.Seen)
                    _expired.Add(pair.Key);
            }

            foreach (ulong key in _expired)
            {
                Release(_gameStrikes[key]);
                _gameStrikes.Remove(key);
            }
        }

        private FastList<WeatherManager.LightningStrike> Queue()
        {
            if (!_queueResolved)
            {
                _queueResolved = true;
                _queueField = typeof(WeatherManager).GetField("m_lightningQueue",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (_queueField == null)
                    Log.Warn("lightning: WeatherManager.m_lightningQueue not found; only visual lightning will show.");
            }

            return _queueField == null
                ? null
                : _queueField.GetValue(Singleton<WeatherManager>.instance) as FastList<WeatherManager.LightningStrike>;
        }

        // ---- our own, visual-only lightning ----------------------------------------------------

        private static bool Overridden
        {
            get { return Settings.LightningOverride != null && Settings.LightningOverride.value; }
        }

        /// <summary>
        /// 0..4. Follows the rain -- nothing below a heavy shower, a proper storm in a
        /// downpour -- unless the player sets it, e.g. for a dry thunderstorm. Either way it
        /// needs cloud to happen in.
        /// </summary>
        /// <remarks>
        /// Both paths reach past 1 now. The override is the Level 2 control, and "Lightning in
        /// storms" is the Level 1 one: if 100% is too few strikes when set by hand, a natural
        /// downpour is too few as well, and the answer to that is a multiplier on what the rain
        /// produces rather than leaving the override on for ever.
        /// </remarks>
        private static float Activity()
        {
            if (CloudWeather.Coverage < 0.15f)
                return 0f;

            if (Overridden)
            {
                return Settings.LightningActivity != null
                    ? Mathf.Clamp(Settings.LightningActivity.value, 0f, 4f) : 0f;
            }

            float fromRain = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1f, CloudWeather.Rain));
            float storms = Settings.LightningStormScale != null
                ? Mathf.Clamp(Settings.LightningStormScale.value, 0f, 4f) : 1f;
            return Mathf.Clamp(fromRain * storms, 0f, 4f);
        }

        private void SpawnOwnStrikes(uint frame, bool paused)
        {
            for (int i = _ownStrikes.Count - 1; i >= 0; i--)
            {
                Strike strike = _ownStrikes[i];
                bool flashing = frame < strike.StartFrame + strike.Profile.Frames;
                bool thunderDue = strike.ThunderIn >= 0f || strike.RollIn >= 0f;

                if (!flashing && !thunderDue)
                {
                    Release(strike);
                    _ownStrikes.RemoveAt(i);
                }
            }

            if (paused)
                return;

            float activity = Activity();
            if (activity <= 0f)
            {
                _pending.Clear();
                return;
            }

            float variety = Variety;
            SpawnFollowUps(frame, variety);

            // Real time, not simulation time: at 3x speed a storm should not strobe. With variety the
            // strikes come in bursts, so the first strikes are fewer by the burst's mean size and the
            // slider's "one every N s" holds on average; a travelling storm far off flashes less.
            float rate = LightningPlacement.StrikesPerSecond(activity) / LightningBursts.ExpectedSize(activity, variety);
            bool cells = Travelling && _cells.Cells.Count > 0;
            if (cells)
                rate *= StormCellRules.RateScale(_cells.Strongest);
            if (_random.NextDouble() > Time.deltaTime * rate)
                return;

            Vector3 camera = _camera.transform.position;
            Vector3 spot = Vector3.zero;
            bool found = false;
            bool inView = false;
            int cellId = 0;

            float ground = Singleton<TerrainManager>.exists
                ? Singleton<TerrainManager>.instance.SampleRawHeightSmooth(camera)
                : 0f;

            // Travelling storms: 85% of the strikes come from a cell (chosen by how active it is);
            // the rest stay all round the camera, as thunder from anywhere belongs in a storm.
            if (cells && _random.NextDouble() < 0.85)
                found = SpotInCell(camera, out spot, out inView, out cellId);

            // Only ever inside cloud -- and mostly where the camera is pointed. The rate
            // was never the reason the storms went unseen; where the strikes were put was.
            //
            // Decided ONCE per strike, then tried in two stages. The first version rolled
            // the dice on every attempt, and from a camera looking steeply down every
            // in-view candidate lands in the same small patch of ground (the ground hit is
            // nearer than the 800 m minimum, so they all clamp to it): with no cloud over
            // that patch, seven attempts in ten were wasted and the strike was skipped --
            // his first log had 2 strikes and 2 skips in 14 s at "one every 3 s". Now an
            // in-view strike gets six tries in the frustum and then carries on all round,
            // so clear sky ahead moves the storm instead of thinning it.
            bool wantView = _random.NextDouble() < LightningPlacement.InViewShare;

            for (int attempt = 0; attempt < 14 && !found; attempt++)
            {
                bool preferView = wantView && attempt < 6;
                spot = LightningPlacement.Spot(camera, _camera.transform.rotation,
                    _camera.fieldOfView, _camera.aspect, ground, preferView,
                    (float)_random.NextDouble(), (float)_random.NextDouble(), (float)_random.NextDouble(),
                    out inView);
                found = CloudAt(spot) > 0.6f;
            }

            if (!found)
            {
                _ownSkippedNoCloud++;
                return;
            }

            // Most lightning stays in the cloud; some of it comes down.
            bool toGround = _random.NextDouble() < 0.35;
            Strike own = AddOwnStrike(frame, spot, toGround, inView, cellId, false);
            if (own == null)
                return;

            // A burst: the follow-ups land near this one, a little later (never from a follow-up).
            LightningBursts.Draw(_random, activity, variety, _burst);
            foreach (LightningBursts.FollowUp f in _burst)
                _pending.Add(new Pending { In = f.Delay, Near = new Vector3(spot.x + f.Dx, spot.y, spot.z + f.Dz), Twin = f.Twin });
        }

        /// <summary>A strike of ours at <paramref name="spot"/> (on the ground: its height is set here).</summary>
        private Strike AddOwnStrike(uint frame, Vector3 spot, bool toGround, bool inView, int cellId, bool followUp)
        {
            Vector3 camera = _camera.transform.position;
            spot.y = Singleton<TerrainManager>.exists
                ? Singleton<TerrainManager>.instance.SampleRawHeightSmooth(spot)
                : 0f;

            Strike own = NewStrike(frame, spot, false, toGround);
            own.FollowUp = followUp;

            float distance = Vector3.Distance(camera, own.Source);
            float variety = Variety;
            float delay = Mathf.Min(distance / SpeedOfSound, variety > 0f ? MaxThunderDelayVariety : MaxThunderDelay);
            // The clap leaves with the return stroke, after the leader has crawled down.
            delay += own.Profile.LeaderFrames / 60f;
            own.ThunderIn = delay;

            // A long strike, or now and then any strike, rolls on after the first clap. With variety a
            // close one cracks and its roll follows almost at once.
            if (variety > 0f && distance < 1200f)
                own.RollIn = delay + Mathf.Lerp(0.2f, 0.5f, (float)_random.NextDouble());
            else if (own.Profile.Frames > 45u || _random.NextDouble() < 0.3)
                own.RollIn = delay + Mathf.Lerp(0.5f, 1.6f, (float)_random.NextDouble());

            if (Log.Detailed)
            {
                LightningFlicker.Profile p = own.Profile;
                Vector3 to = spot - camera;
                Log.Detail("lightning: own " + (own.Kind >= 0 ? ((LightningBoltShape.Kind)own.Kind).ToString() : toGround ? "bolt" : "sheet") +
                           (followUp ? " (burst)" : "") + (inView ? " in view" : " all-round") +
                           (cellId > 0 ? " from cell #" + cellId : "") +
                           " at (" + spot.x.ToString("F0") + ", " + spot.z.ToString("F0") + "), " +
                           Vector3.Distance(camera, spot).ToString("F0") + " m " + StormCells.Bearing(to) + " of the camera" +
                           ", lasts " + (p.Frames / 60f).ToString("F2") + "s" +
                           (p.LeaderFrames > 0 ? " (leader " + p.LeaderFrames + " frames)" : "") + ", " + p.Strokes + " stroke(s), flicker every " +
                           p.Reseed + " frames, power " + p.Power.ToString("F2") + ", reach " + p.Reach.ToString("F0") + " m, tint (" +
                           p.Tint.r.ToString("F2") + ", " + p.Tint.g.ToString("F2") + ", " + p.Tint.b.ToString("F2") + ")");
            }

            _ownStrikes.Add(own);
            _ownSpawned++;
            if (inView)
                _ownInView++;
            if (cellId > 0)
                _ownInCell++;
            if (followUp)
                _ownFollowUps++;
            return own;
        }

        /// <summary>Bursts: the follow-ups whose time has come land near their first strike, if there is cloud.</summary>
        private void SpawnFollowUps(uint frame, float variety)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Pending p = _pending[i];
                p.In -= Time.deltaTime;
                if (p.In > 0f)
                {
                    _pending[i] = p;
                    continue;
                }

                _pending.RemoveAt(i);
                if (variety <= 0f || CloudAt(p.Near) <= 0.45f)
                    continue;

                bool toGround = p.Twin || _random.NextDouble() < 0.45;
                Vector3 camera = _camera.transform.position;
                Vector2 kept = StormCellRules.KeepAway(camera.x, camera.z, new Vector2(p.Near.x, p.Near.z));
                AddOwnStrike(frame, new Vector3(kept.x, p.Near.y, kept.y), toGround, false, 0, true);
            }
        }

        /// <summary>
        /// A spot inside a storm cell, the cell chosen by how active it is. While a cell is in view,
        /// 60% of its strikes are tried in the part of it the camera sees.
        /// </summary>
        private bool SpotInCell(Vector3 camera, out Vector3 spot, out bool inView, out int cellId)
        {
            spot = Vector3.zero;
            inView = false;
            cellId = 0;

            float total = 0f;
            foreach (StormCells.Cell c in _cells.Cells)
                total += c.Envelope;
            if (total <= 0f)
                return false;

            float pick = (float)_random.NextDouble() * total;
            StormCells.Cell cell = _cells.Cells[0];
            foreach (StormCells.Cell c in _cells.Cells)
            {
                cell = c;
                pick -= c.Envelope;
                if (pick <= 0f)
                    break;
            }

            Vector3 centre = cell.Position(camera.y);
            float half = StormCellRules.HalfWedge(_camera.fieldOfView, _camera.aspect);
            Vector3 heading = _camera.transform.forward;
            bool wantView = cell.InView && _random.NextDouble() < 0.6;

            for (int attempt = 0; attempt < 12; attempt++)
            {
                Vector2 s = StormCellRules.SpotIn(centre.x, centre.z, cell.Radius,
                    (float)_random.NextDouble(), (float)_random.NextDouble());
                s = StormCellRules.KeepAway(camera.x, camera.z, s);
                bool seen = StormCellRules.InWedge(camera.x, camera.z, heading.x, heading.z, half, s.x, s.y);
                if (wantView && !seen && attempt < 8)
                    continue;

                Vector3 candidate = new Vector3(s.x, 0f, s.y);
                if (CloudAt(candidate) <= 0.45f)
                    continue;

                spot = candidate;
                inView = seen;
                cellId = cell.Id;
                return true;
            }

            return false;
        }

        // ---- shared ---------------------------------------------------------------------------

        private Strike NewStrike(uint frame, Vector3 ground, bool fromGame, bool withBolt)
        {
            float bottom = Settings.CloudAltitude != null ? Settings.CloudAltitude.value : Settings.Defaults.Altitude;
            // The height the clouds actually reach: the layer's thickness, or the Cumulus style's
            // tallest cloud (CloudShaderParams.LayerHeight).
            float thickness = CloudShaderParams.LayerHeight;

            System.Random random = new System.Random(unchecked((int)(frame * 2654435761u) ^ ground.GetHashCode()));
            // Where in the cloud it starts varies too: a stroke to the ground leaves the lower
            // part of the layer, sheet lightning can be anywhere inside it.
            float depth = withBolt
                ? Mathf.Lerp(0.18f, 0.45f, (float)random.NextDouble())
                : Mathf.Lerp(0.3f, 0.75f, (float)random.NextDouble());

            Vector3 source = new Vector3(
                ground.x + ((float)random.NextDouble() - 0.5f) * 500f,
                bottom + Mathf.Max(50f, thickness) * depth,
                ground.z + ((float)random.NextDouble() - 0.5f) * 500f);

            // Lightning variety (2026-11-15): a kind of channel per strike. 0% = the 1.5.3 code paths.
            float variety = Variety;
            int kind = -1;
            if (variety > 0f)
            {
                bool underBase = _camera != null && _camera.transform.position.y < bottom;
                kind = LightningBoltShape.Pick(random.NextDouble(), variety, withBolt, fromGame, underBase);
            }

            // No two alike. The game's strikes keep the game's timing (its ground lights and
            // thunder run on it); ours vary in everything.
            Strike strike = new Strike
            {
                StartFrame = frame,
                Ground = ground,
                Source = source,
                Profile = fromGame
                    ? LightningFlicker.Profile.ForGameStrike(random)
                    : variety > 0f
                        ? LightningFlicker.Profile.Random(random, withBolt, kind, variety)
                        : LightningFlicker.Profile.Random(random, withBolt),
                FromGame = fromGame,
                Kind = kind,
            };

            if (fromGame && variety > 0f)
                strike.Profile.Tint = LightningFlicker.Profile.VarietyTint(random, variety, strike.Profile.Tint);

            if (_boltMaterial != null && source.y > ground.y + 50f)
            {
                if (kind >= 0)
                    strike.Bolt = LightningBoltMesh.Build((LightningBoltShape.Kind)kind, source, ground, bottom, random);
                else if (withBolt)
                    strike.Bolt = LightningBoltMesh.Build(source, ground, random);
            }

            return strike;
        }

        /// <summary>Cloud amount above a point, 0..1, from the same field and wind as the clouds.</summary>
        private float CloudAt(Vector3 position)
        {
            float tile = _field.Tile;
            Vector2 phase = CloudWind.WeatherPhase(tile);
            return _field.SampleCloud(position.x / tile - phase.x,
                                      position.z / tile - phase.y,
                                      CloudStyle.CloudTestCoverage(CloudWeather.Coverage));
        }

        private void Evaluate(uint gameFrame, uint ownFrame, bool paused)
        {
            // The game damps rapid flashing (m_flashingReduction) but only knows its own
            // strikes. Ours get the same treatment, from the same rule, over everything.
            float reduction = Mathf.Max(_reduction, Singleton<WeatherManager>.instance.m_flashingReduction);

            _active.Clear();
            float total = 0f;

            foreach (Strike strike in _gameStrikes.Values)
                total += Activate(strike, gameFrame, reduction);
            foreach (Strike strike in _ownStrikes)
                total += Activate(strike, ownFrame, reduction);

            if (!paused)
            {
                _reduction = Mathf.Clamp01(_reduction + Mathf.Abs(total - _lastTotal) * 0.1f - Time.deltaTime * 0.3f);
                _lastTotal = total;
            }

            // The strongest few reach the shader.
            _active.Sort((a, b) => (b.Intensity * b.Power).CompareTo(a.Intensity * a.Power));

            float brightness = Settings.LightningBrightness != null ? Mathf.Max(0f, Settings.LightningBrightness.value) : 1f;
            _flashCount = Mathf.Min(_active.Count, MaxFlashes);

            for (int i = 0; i < MaxFlashes; i++)
            {
                if (i < _flashCount)
                {
                    Strike strike = _active[i];
                    Color colour = strike.Profile.Tint * (5f * brightness * strike.Power * strike.Intensity);
                    FlashPos[i] = new Vector4(strike.Source.x, strike.Source.y, strike.Source.z, strike.Profile.Reach);
                    FlashColor[i] = new Vector4(colour.r, colour.g, colour.b, 0f);
                }
                else
                {
                    FlashPos[i] = new Vector4(0f, 0f, 0f, 1f);
                    FlashColor[i] = Vector4.zero;
                }
            }

            PlayThunder(paused);
        }

        private float Activate(Strike strike, uint frame, float reduction)
        {
            strike.Intensity = 0f;
            if (!strike.Flashing(frame))
                return 0f;

            strike.Intensity = LightningFlicker.Intensity(frame, strike.StartFrame, reduction, strike.Profile);
            _active.Add(strike);
            return strike.Intensity;
        }

        /// <summary>
        /// The game thunders for its own strikes (StrikeNow). Ours borrow the same sound, the
        /// same way, after the time the sound would take to arrive.
        /// </summary>
        private void PlayThunder(bool paused)
        {
            if (paused)
                return;

            _thunderReady -= Time.deltaTime;

            Strike clap = null;
            float nearest = float.MaxValue;
            Vector3 camera = _camera.transform.position;

            foreach (Strike strike in _ownStrikes)
            {
                // The clap: louder for a powerful strike and for one that reaches the ground,
                // quieter the further off it struck. Only one gets through per cooldown, and it
                // is the nearest -- the one the ear would pick out of a storm anyway.
                if (Due(ref strike.ThunderIn))
                {
                    float distance = Vector3.Distance(camera, strike.Source);
                    if (distance < nearest)
                    {
                        nearest = distance;
                        clap = strike;
                    }
                }

                // The roll that follows some of them: quieter, and pitched well down. Only
                // three strikes in ten have one, so these do not need limiting.
                if (Due(ref strike.RollIn))
                {
                    float far = LightningPlacement.ThunderVolume(Vector3.Distance(camera, strike.Source));
                    Thunder(strike, 0.35f * far, _soundRandomizer.Int32(450, 700) * 0.001f);
                }
            }

            if (clap == null || _thunderReady > 0f)
                return;

            _thunderReady = ThunderCooldown;
            float volume = Mathf.Clamp01((clap.Bolt != null ? 0.7f : 0.45f) * (0.6f + 0.5f * clap.Profile.Power))
                         * LightningPlacement.ThunderVolume(nearest);
            int low = 620, high = 1150;
            if (Variety > 0f)
            {
                // The one sound the game has, pitched by distance: a sharp crack close by, a low
                // quiet rumble far off, as Lightning variety's thunder.
                if (nearest < 1200f)
                {
                    low = 1050;
                    high = 1300;
                }
                else if (nearest > 4000f)
                {
                    low = 450;
                    high = 700;
                    volume *= 0.8f;
                }
            }
            Thunder(clap, volume, _soundRandomizer.Int32(low, high) * 0.001f);
        }

        /// <summary>Counts a timer down; true on the frame it runs out. Negative means not pending.</summary>
        private static bool Due(ref float timer)
        {
            if (timer < 0f)
                return false;

            timer -= Time.deltaTime;
            if (timer > 0f)
                return false;

            timer = -1f;
            return true;
        }

        private void Thunder(Strike strike, float volume, float pitch)
        {
            WeatherProperties properties = Singleton<WeatherManager>.instance.m_properties;
            if (properties == null || properties.m_lightningSound == null || !Singleton<AudioManager>.exists)
                return;

            try
            {
                AudioManager audio = Singleton<AudioManager>.instance;
                bool landed = strike.Bolt != null && (strike.Kind < 0 || LightningBoltShape.ReachesGround((LightningBoltShape.Kind)strike.Kind));
                audio.AddEvent(audio.EffectGroup, properties.m_lightningSound.GetVariation(ref _soundRandomizer),
                               landed ? strike.Ground : strike.Source, Vector3.zero,
                               10000f, volume, pitch);
            }
            catch (Exception e)
            {
                Log.Error("Playing thunder threw.", e);
            }
        }

        private void DrawBolts()
        {
            if (_boltMaterial == null)
                return;

            float pixelAngle = 2f * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, _camera.pixelHeight);
            _boltMaterial.SetFloat(IdPixelAngle, pixelAngle);

            // Drawn BEFORE the clouds, as in 1.2: the cloud pass lays the cloud, the rain and the fog
            // over the channel, so its top fades into the cloud it comes out of. Just before whichever
            // queue the clouds are in -- 3090 before the one draw (the shader's own Transparent+90),
            // 2510 before their early draw under the base, after the game's sky (2501-2503). 1.3.0
            // drew it after them, dimmed by only what is in front, and in-game it looked "in front"
            // of the cloud (the author, 2026-09-30: "Not a fan"). Since 1.5.0 the cloud pass stops a
            // little behind a bolt on its pixels (BoltMap): a cloud far behind no longer cuts it.
            _boltMaterial.renderQueue = CloudVolume.CloudsQueue - BoltBeforeClouds;

            float brightness = Settings.LightningBrightness != null ? Mathf.Max(0f, Settings.LightningBrightness.value) : 1f;
            bool replaceGame = _gameBoltHidden;
            bool drawn = false;
            _drawnBolts.Clear();

            foreach (Strike strike in _active)
            {
                if (strike.Bolt == null || strike.Intensity <= 0.01f)
                    continue;

                // The game's strike keeps the game's bolt unless ours is replacing it.
                if (strike.FromGame && !replaceGame)
                    continue;

                // Per strike, so each bolt has the colour of its own flash in the cloud. With variety,
                // seen through the air between: close ones blue-white, far ones dimmer and pinker.
                Color colour = strike.Profile.Tint * (22f * brightness);
                float variety = Variety;
                if (variety > 0f)
                    colour *= LightningBursts.DistanceTint(Vector3.Distance(_camera.transform.position, strike.Ground), variety);

                // The leader (x on, y revealed share of the path), and the forks' fade (w). Zero = all
                // shown, as every 1.5.3 bolt and every game strike.
                uint frameNow = strike.FromGame ? Singleton<SimulationManager>.instance.m_referenceFrameIndex : (uint)_ownClock;
                float reveal = LightningFlicker.LeaderReveal(frameNow, strike.StartFrame, strike.Profile);
                float forkFade = LightningFlicker.ForkFade(frameNow, strike.StartFrame, strike.Profile);
                float intensity = strike.Intensity * strike.Power;
                if (reveal >= 0f)
                    intensity = 0.35f * strike.Power;   // the leader: faint, its tip brighter (shader)

                _block.Clear();
                _block.SetVector(IdBoltColor, new Vector4(colour.r, colour.g, colour.b, 0f));
                _block.SetFloat(IdBoltIntensity, intensity);
                _block.SetVector(IdBoltLeader, new Vector4(reveal >= 0f ? 1f : 0f, Mathf.Max(0f, reveal), 0f, forkFade));
                Graphics.DrawMesh(strike.Bolt, Matrix4x4.identity, _boltMaterial, 0, null, 0, _block);
                _drawnBolts.Add(strike.Bolt);
                drawn = true;
            }

            // Where they are, for the cloud pass to stop just behind them (none: it stops reading).
            _boltMap.Draw(_camera, _drawnBolts, pixelAngle);

            // Once per queue a bolt is drawn at, read back from the material (1.3.0's line said
            // "after the clouds" while a copied queue had really put the bolt in theirs).
            if (drawn && _boltMaterial.renderQueue != _boltQueueLogged)
            {
                _boltQueueLogged = _boltMaterial.renderQueue;
                Log.Msg("lightning: bolts drawn at queue " + _boltQueueLogged + ", just before the clouds (" +
                        CloudVolume.CloudsQueue + "), as in 1.2: the clouds, rain and fog are laid over them, up to " +
                        (BoltMap.BehindShare * 100f).ToString("F0") + "% of a bolt's distance behind it");
            }
        }

        /// <summary>
        /// The game reads WeatherProperties.m_lightningMaterial at draw time, in the same block
        /// that draws the strike's ground lights -- so the material is swapped rather than the
        /// mesh nulled, which would take the lights with it.
        /// </summary>
        private void HideGameBolt(bool hide)
        {
            WeatherProperties properties = Singleton<WeatherManager>.instance.m_properties;
            if (properties == null)
                return;

            if (hide)
            {
                if (!_gameBoltHidden)
                {
                    _gameBoltMaterial = properties.m_lightningMaterial;
                    _gameBoltHidden = true;
                    Log.Msg("lightning: game bolt hidden (its material was " +
                            (_gameBoltMaterial == null ? "none" : "'" + _gameBoltMaterial.name + "'") +
                            "); its ground lights and thunder are untouched");
                }

                if (properties.m_lightningMaterial != _invisible)
                    properties.m_lightningMaterial = _invisible;
            }
            else if (_gameBoltHidden)
            {
                _gameBoltHidden = false;
                if (_gameBoltMaterial != null)
                    properties.m_lightningMaterial = _gameBoltMaterial;

                Log.Msg("lightning: game bolt restored");
            }
        }

        private void Clear()
        {
            foreach (Strike strike in _gameStrikes.Values)
                Release(strike);
            foreach (Strike strike in _ownStrikes)
                Release(strike);

            _gameStrikes.Clear();
            _ownStrikes.Clear();
            _active.Clear();
            _pending.Clear();
            _cells.Clear("lightning off or the weather hidden");
            _flashCount = 0;

            if (_boltMap != null)
                _boltMap.Off();
        }

        private static void Release(Strike strike)
        {
            if (strike.Bolt != null)
            {
                Destroy(strike.Bolt);
                strike.Bolt = null;
            }
        }

        private void OnDestroy()
        {
            Clear();

            if (Singleton<WeatherManager>.exists)
                HideGameBolt(false);

            if (_boltMaterial != null)
                Destroy(_boltMaterial);
            if (_boltMap != null)
                _boltMap.Dispose();
            if (_invisible != null)
                Destroy(_invisible);
        }
    }
}
