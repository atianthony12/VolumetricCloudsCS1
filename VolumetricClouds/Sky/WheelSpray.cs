using System;
using ColossalFramework;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// "Spray from wheels" (2026-11-15, the author: "car wheels effects during rain"): on wet roads the
    /// nearest moving cars throw a rooster tail of droplets, fine spray and a trail of mist that hangs
    /// behind and drifts off on the wind; more the faster they go. Visual only. Rules in
    /// <see cref="WheelSprayRules"/>, the look in WheelSpray.shader.
    /// </summary>
    /// <remarks>
    /// Which vehicles: every 4th frame the game's vehicle grid (32 m cells) is walked within 160 m of the
    /// camera, on the main thread as the game's own renderer does (the buffer is the simulation's: the
    /// chain is guarded, every flag read again each frame because ids are reused). Cars and trolleybuses,
    /// spawned, moving over 4 m/s, not parked, waiting, underground, in a building or flying.
    ///
    /// The particles are a CPU pool (struct arrays, nothing allocated per frame) drawn as one dynamic mesh
    /// with Graphics.DrawMesh: a trail that hangs where it was thrown needs each particle's own birth place.
    /// World space: the mist stays while the car drives on. Simulation time: paused, nothing moves or is born.
    ///
    /// Off means nothing runs: the row at 0, the ground dry, a winter map, the weather hidden or the camera
    /// high up -- the pool and the mesh are freed.
    /// </remarks>
    public class WheelSpray : MonoBehaviour
    {
        private const int PoolSize = 2048;
        private const float Reach = 160f;
        private const float HighestCamera = 400f;

        private struct Particle
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float Age;
            public float Life;
            public int Kind;
            public float Floor;
            public float Seed;
        }

        private struct Kept
        {
            public ushort Id;
            public float Distance;
            public float Carry;   // emission owed (fractions of a particle)
            public bool Large;
            public float WheelBase, Gauge;
        }

        private Particle[] _pool;
        private int _next;
        private int _alive;
        private Kept[] _kept;
        private int _keptCount;
        private Mesh _mesh;
        private Material _material;
        private Vector3[] _vertices;
        private Vector3[] _normals;
        private Vector2[] _corners;
        private Vector2[] _looks;
        private int _frame;
        private System.Random _random = new System.Random(20261115);
        private string _loggedState;
        private float _nextLog;
        private int _born;
        private bool _missingLogged;

        private static readonly int IdLight = Shader.PropertyToID("_SprayLight");

        private static float Row
        {
            get { return Settings.WheelSpray != null ? Mathf.Clamp(Settings.WheelSpray.value, 0f, 3f) : Settings.Defaults.WheelSpray; }
        }

        private void LateUpdate()
        {
            float row = Row;
            Camera camera = GameCamera.Get();
            float wetness = WetGround.GroundWetness;

            string why = null;
            if (row <= 0f) why = "the row is at 0%";
            else if (!Settings.WeatherShown) why = "the weather is hidden";
            else if (CloudRain.IsSnow) why = "a winter map";
            else if (wetness <= 0.15f) why = "the roads are dry";
            else if (camera == null || !Singleton<VehicleManager>.exists) why = "no camera or vehicles";
            else if (HeightAboveGround(camera) > HighestCamera) why = "the camera is high over the city";
            else if (Singleton<InfoManager>.exists && Singleton<InfoManager>.instance.CurrentMode != InfoManager.InfoMode.None) why = "an info view is open";

            if (why != null)
            {
                Free(why);
                return;
            }

            if (!EnsureMade())
            {
                Free("its shader is not in this bundle");
                return;
            }

            SimulationManager simulation = Singleton<SimulationManager>.instance;
            bool paused = simulation.SimulationPaused || simulation.ForcedSimulationPaused;
            float dt = paused ? 0f : Time.deltaTime * Mathf.Min(2, Mathf.Max(1, simulation.FinalSimulationSpeed));

            Vector3 eye = camera.transform.position;
            if ((_frame++ & 3) == 0)
                SelectVehicles(eye);

            float near = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(250f, HighestCamera, HeightAboveGround(camera)));
            if (dt > 0f)
            {
                Emit(row * near, wetness, dt);
                Advance(dt);
            }

            Draw(camera);

            if (_loggedState != "on")
            {
                _loggedState = "on";
                Log.Msg("wheel spray: on -- " + _keptCount + " moving vehicle(s) within " + Reach.ToString("F0") + " m, roads " +
                        (wetness * 100f).ToString("F0") + "% wet, row " + (row * 100f).ToString("F0") + "%");
            }

            if (Log.Detailed && Time.time >= _nextLog)
            {
                _nextLog = Time.time + 5f;
                Log.Detail("wheel spray: " + _keptCount + " vehicles, " + _alive + " particles alive, " + (_born / 5) + " born/s");
                _born = 0;
            }
        }

        private static float HeightAboveGround(Camera camera)
        {
            Vector3 p = camera.transform.position;
            float ground = Singleton<TerrainManager>.exists ? Singleton<TerrainManager>.instance.SampleRawHeightSmooth(p) : 0f;
            return p.y - ground;
        }

        private bool EnsureMade()
        {
            if (_mesh != null)
                return true;

            // Looked up once: ShaderBundle.Get warns on every call that finds nothing.
            if (_missingLogged)
                return false;
            Shader shader = ShaderBundle.Get(ShaderBundle.WheelSpray);
            if (shader == null)
            {
                _missingLogged = true;
                Log.Msg("wheel spray: NOT AVAILABLE here (its shader is not in this platform's bundle or not supported)");
                return false;
            }

            _material = new Material(shader) { name = "VolumetricCloudsWheelSpray" };
            _pool = new Particle[PoolSize];
            _kept = new Kept[24];
            _vertices = new Vector3[PoolSize * 4];
            _normals = new Vector3[PoolSize * 4];
            _corners = new Vector2[PoolSize * 4];
            _looks = new Vector2[PoolSize * 4];
            int[] triangles = new int[PoolSize * 6];
            for (int i = 0; i < PoolSize; i++)
            {
                int v = i * 4;
                _corners[v] = new Vector2(-1f, -1f);
                _corners[v + 1] = new Vector2(1f, -1f);
                _corners[v + 2] = new Vector2(1f, 1f);
                _corners[v + 3] = new Vector2(-1f, 1f);
                int t = i * 6;
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v; triangles[t + 4] = v + 2; triangles[t + 5] = v + 3;
            }

            _mesh = new Mesh { name = "VolumetricCloudsWheelSpray" };
            _mesh.MarkDynamic();
            _mesh.vertices = _vertices;
            _mesh.normals = _normals;
            _mesh.uv = _corners;
            _mesh.uv2 = _looks;
            _mesh.triangles = triangles;
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
            Log.Msg("wheel spray: made (pool " + PoolSize + ")");
            return true;
        }

        private void Free(string why)
        {
            if (_mesh == null)
                return;

            Destroy(_mesh);
            Destroy(_material);
            _mesh = null;
            _material = null;
            _pool = null;
            _kept = null;
            _vertices = null;
            _normals = null;
            _corners = null;
            _looks = null;
            _alive = 0;
            _keptCount = 0;
            if (why != _loggedState)
            {
                _loggedState = why;
                Log.Msg("wheel spray: freed (" + why + ")");
            }
        }

        /// <summary>The nearest moving road vehicles within <see cref="Reach"/>, by the game's grid.</summary>
        private void SelectVehicles(Vector3 eye)
        {
            int wanted = Settings.QualityPreset != null && Settings.QualityPreset.value <= 0 ? 6
                       : Settings.QualityPreset != null && Settings.QualityPreset.value == 1 ? 12 : 24;
            VehicleManager manager = Singleton<VehicleManager>.instance;
            Vehicle[] buffer = manager.m_vehicles.m_buffer;
            ushort[] grid = manager.m_vehicleGrid;
            const int Resolution = 540;
            const float Cell = 32f;

            int minX = Mathf.Clamp((int)((eye.x - Reach) / Cell + Resolution / 2f), 0, Resolution - 1);
            int maxX = Mathf.Clamp((int)((eye.x + Reach) / Cell + Resolution / 2f), 0, Resolution - 1);
            int minZ = Mathf.Clamp((int)((eye.z - Reach) / Cell + Resolution / 2f), 0, Resolution - 1);
            int maxZ = Mathf.Clamp((int)((eye.z + Reach) / Cell + Resolution / 2f), 0, Resolution - 1);

            // Keep the carry of vehicles that stay, so a car's spray does not stutter at each selection.
            int oldCount = _keptCount;
            Kept[] old = _oldKept ?? (_oldKept = new Kept[24]);
            Array.Copy(_kept, old, oldCount);
            _keptCount = 0;

            try
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        ushort id = grid[z * Resolution + x];
                        int guard = 0;
                        while (id != 0 && guard++ < 16384)
                        {
                            ushort next = buffer[id].m_nextGridVehicle;
                            Consider(buffer, id, eye, wanted, old, oldCount);
                            id = next;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("wheel spray: reading the vehicles threw", e);
                _keptCount = 0;
            }
        }

        private Kept[] _oldKept;

        private void Consider(Vehicle[] buffer, ushort id, Vector3 eye, int wanted, Kept[] old, int oldCount)
        {
            if (!Usable(buffer, id))
                return;

            Vector3 position = buffer[id].GetLastFramePosition();
            float distance = (position - eye).magnitude;
            if (distance > Reach)
                return;

            int slot = _keptCount;
            if (_keptCount >= wanted)
            {
                // Replace the furthest if this one is nearer.
                int furthest = 0;
                for (int i = 1; i < _keptCount; i++)
                    if (_kept[i].Distance > _kept[furthest].Distance)
                        furthest = i;
                if (_kept[furthest].Distance <= distance)
                    return;
                slot = furthest;
            }
            else
            {
                _keptCount++;
            }

            VehicleInfo info = buffer[id].Info;
            VehicleInfoGen gen = info != null ? info.m_generatedInfo : null;
            float carry = 0f;
            for (int i = 0; i < oldCount; i++)
                if (old[i].Id == id)
                    carry = old[i].Carry;

            _kept[slot] = new Kept
            {
                Id = id,
                Distance = distance,
                Carry = carry,
                Large = info != null && info.m_isLargeVehicle,
                WheelBase = gen != null && gen.m_wheelBase > 0.5f ? gen.m_wheelBase : (gen != null ? gen.m_size.z * 0.6f : 2.6f),
                Gauge = gen != null && gen.m_wheelGauge > 0.5f ? gen.m_wheelGauge : (gen != null ? gen.m_size.x * 0.8f : 1.6f),
            };
        }

        private static bool Usable(Vehicle[] buffer, ushort id)
        {
            Vehicle.Flags flags = buffer[id].m_flags;
            const Vehicle.Flags Needed = Vehicle.Flags.Created | Vehicle.Flags.Spawned;
            const Vehicle.Flags Not = Vehicle.Flags.Deleted | Vehicle.Flags.WaitingPath | Vehicle.Flags.Stopped | Vehicle.Flags.Parking |
                                      Vehicle.Flags.Underground | Vehicle.Flags.Transition | Vehicle.Flags.InsideBuilding | Vehicle.Flags.Flying;
            if ((flags & Needed) != Needed || (flags & Not) != 0)
                return false;

            VehicleInfo info = buffer[id].Info;
            if (info == null)
                return false;
            VehicleInfo.VehicleType type = info.m_vehicleType;
            return type == VehicleInfo.VehicleType.Car || type == VehicleInfo.VehicleType.Trolleybus;
        }

        private void Emit(float row, float wetness, float dt)
        {
            Vehicle[] buffer = Singleton<VehicleManager>.instance.m_vehicles.m_buffer;
            for (int k = 0; k < _keptCount; k++)
            {
                ushort id = _kept[k].Id;
                if (!Usable(buffer, id))
                    continue;

                Vector3 position;
                Quaternion rotation;
                buffer[id].GetSmoothPosition(id, out position, out rotation);
                Vector3 velocity = buffer[id].GetSmoothVelocity(id);
                float speed = new Vector2(velocity.x, velocity.z).magnitude;
                float amount = WheelSprayRules.Amount(speed, wetness) * row * (_kept[k].Large ? 2f : 1f);
                if (amount <= 0f)
                    continue;

                Vector3 left, right;
                WheelSprayRules.RearWheels(position, velocity, _kept[k].WheelBase, _kept[k].Gauge, out left, out right);

                for (int kind = 0; kind < 3; kind++)
                {
                    // Two wheels; the owed fraction is kept per vehicle (one carry for all kinds, scaled).
                    float owed = WheelSprayRules.Rate(kind) * amount * dt * 2f;
                    int count = (int)owed;
                    if (_random.NextDouble() < owed - count)
                        count++;
                    for (int c = 0; c < count; c++)
                        Spawn(kind, (c & 1) == 0 ? left : right, velocity, position.y);
                }
            }
        }

        private void Spawn(int kind, Vector3 at, Vector3 velocity, float floor)
        {
            float r1 = (float)_random.NextDouble(), r2 = (float)_random.NextDouble(), r3 = (float)_random.NextDouble();
            Particle p;
            p.Position = at + new Vector3((r1 - 0.5f) * 0.3f, 0.15f + 0.2f * r2, (r3 - 0.5f) * 0.3f);
            p.Velocity = WheelSprayRules.Launch(kind, velocity, r1, r2, r3);
            p.Age = 0f;
            p.Life = WheelSprayRules.Life(kind, r2);
            p.Kind = kind;
            p.Floor = floor;
            p.Seed = r3;
            _pool[_next] = p;
            _next = (_next + 1) % PoolSize;
            _born++;
        }

        private void Advance(float dt)
        {
            Vector3 wind = CloudWeather.WindDirection * (WeatherWind.Advanced ? WeatherWind.Speed : 3f) * 0.5f;
            _alive = 0;
            for (int i = 0; i < PoolSize; i++)
            {
                if (_pool[i].Life <= 0f)
                    continue;
                Particle p = _pool[i];
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    p.Life = 0f;
                    _pool[i] = p;
                    continue;
                }
                p.Velocity = WheelSprayRules.Step(p.Kind, p.Velocity, wind, dt);
                p.Position += p.Velocity * dt;
                if (p.Position.y < p.Floor && p.Kind == WheelSprayRules.Droplet)
                    p.Life = 0f;
                if (p.Position.y < p.Floor)
                    p.Position.y = p.Floor;
                _pool[i] = p;
                _alive++;
            }
        }

        private void Draw(Camera camera)
        {
            for (int i = 0; i < PoolSize; i++)
            {
                Particle p = _pool[i];
                int v = i * 4;
                float life = p.Life > 0f ? p.Age / p.Life : 1f;
                float opacity = p.Life > 0f ? WheelSprayRules.Opacity(p.Kind, life) : 0f;
                Vector2 look = new Vector2(WheelSprayRules.Size(p.Kind, life), opacity + p.Kind * 2f);
                Vector3 stretch = p.Kind == WheelSprayRules.Droplet ? p.Velocity * 0.03f : Vector3.zero;
                for (int c = 0; c < 4; c++)
                {
                    _vertices[v + c] = p.Position;
                    _normals[v + c] = stretch;
                    _looks[v + c] = look;
                }
            }

            _mesh.vertices = _vertices;
            _mesh.normals = _normals;
            _mesh.uv2 = _looks;

            _material.SetColor(IdLight, Lit());
            Graphics.DrawMesh(_mesh, Matrix4x4.identity, _material, 0);
        }

        /// <summary>The spray's light: the city's air (ambient), a little sun, as the rain streaks'.</summary>
        private static Color Lit()
        {
            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Color ambient = SceneAmbient.Air();
            if (linear)
                ambient = ambient.linear;
            Color colour = ambient * 1.3f;
            DayNightProperties properties = DayNightProperties.instance;
            Light sun = properties != null && properties.m_SunLight != null ? properties.m_SunLight.GetComponent<Light>() : null;
            if (sun != null)
                colour += (linear ? sun.color.linear : sun.color) * (sun.intensity * 0.05f);
            return new Color(Mathf.Clamp(colour.r, 0.04f, 1.5f), Mathf.Clamp(colour.g, 0.04f, 1.5f), Mathf.Clamp(colour.b, 0.05f, 1.5f), 1f);
        }

        private void OnDestroy()
        {
            Free("the city closes");
        }
    }
}
