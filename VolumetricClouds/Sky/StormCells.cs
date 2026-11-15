using System.Collections.Generic;
using UnityEngine;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// "Storms move" = Travelling storms (2026-11-15): the storm cells our own lightning comes from.
    /// Rules in <see cref="StormCellRules"/>; this keeps the cells, moves them with the clouds and
    /// logs their lives. Never saved, never seen by a shader (invariant 13: a cell is a C# point in
    /// doubles, riding the same drift every cloud lookup reads), and it reads the one wind
    /// (invariant 2) without moving anything.
    /// </summary>
    public sealed class StormCells
    {
        public sealed class Cell
        {
            public int Id;
            /// <summary>Where it is in the cloud field's frame: world = this + the drift + Steered.</summary>
            public double FrameX, FrameZ;
            public double SteeredX, SteeredZ;
            public Vector2 Steer;
            public float Radius;
            public float Age;
            public float Envelope;
            public float Along;
            public bool InView;

            public Vector3 Position(float y)
            {
                return new Vector3((float)(FrameX + DriftX + SteeredX), y, (float)(FrameZ + DriftZ + SteeredZ));
            }
        }

        private readonly List<Cell> _cells = new List<Cell>();
        private int _nextId = 1;
        private float _birthCooldown;
        private bool _loggedNoCloud;
        private double _lastDriftX, _lastDriftZ;
        private bool _haveDrift;
        private float _driftSpeed;

        public List<Cell> Cells
        {
            get { return _cells; }
        }

        /// <summary>The clouds' own speed over the ground, m/s (smoothed).</summary>
        public float DriftSpeed
        {
            get { return _driftSpeed; }
        }

        private static double DriftX
        {
            get { return CloudWind.X + CloudWind.ShiftX; }
        }

        private static double DriftZ
        {
            get { return CloudWind.Z + CloudWind.ShiftZ; }
        }

        /// <summary>The largest envelope of any cell now, 0 with none.</summary>
        public float Strongest
        {
            get
            {
                float best = 0f;
                foreach (Cell c in _cells)
                    best = Mathf.Max(best, c.Envelope);
                return best;
            }
        }

        public void Clear(string why)
        {
            if (_cells.Count > 0)
                Log.Msg("lightning: storm cells cleared (" + why + ")");
            _cells.Clear();
            _haveDrift = false;
        }

        /// <summary>
        /// Once a frame while Travelling storms is on: ages, steers and ends the cells, and gives
        /// birth to new ones upwind of <paramref name="lookAt"/> while the storm wants more.
        /// </summary>
        /// <param name="simSeconds">Simulation seconds this frame (0 while paused).</param>
        /// <param name="cloudAt">Cloud over a point, 0..1 (the lightning's own test).</param>
        public void Advance(float simSeconds, float realSeconds, Vector3 lookAt, Vector3 wind, float activity,
            Camera camera, System.Func<Vector3, float> cloudAt, System.Random random)
        {
            // The clouds' speed from the drift itself: whatever moves it (the speed row, the game's
            // wind, a paused city) is already in it.
            double dx = DriftX, dz = DriftZ;
            if (_haveDrift && realSeconds > 1e-4f)
            {
                float step = (float)System.Math.Sqrt((dx - _lastDriftX) * (dx - _lastDriftX) + (dz - _lastDriftZ) * (dz - _lastDriftZ));
                float speed = simSeconds > 0f ? step / simSeconds : _driftSpeed;
                _driftSpeed = Mathf.Lerp(_driftSpeed, Mathf.Min(speed, 200f), Mathf.Clamp01(realSeconds * 0.5f));
            }
            _lastDriftX = dx;
            _lastDriftZ = dz;
            _haveDrift = true;

            Vector3 w = new Vector3(wind.x, 0f, wind.z);
            if (w.sqrMagnitude < 1e-6f)
                w = CloudWind.DefaultDirection;
            w.Normalize();

            float half = camera != null ? StormCellRules.HalfWedge(camera.fieldOfView, camera.aspect) : 0.5f;
            Vector3 eye = camera != null ? camera.transform.position : lookAt;
            Vector3 heading = camera != null ? camera.transform.forward : w;

            for (int i = _cells.Count - 1; i >= 0; i--)
            {
                Cell c = _cells[i];
                c.Age += simSeconds;
                c.SteeredX += c.Steer.x * simSeconds;
                c.SteeredZ += c.Steer.y * simSeconds;

                Vector3 p = c.Position(lookAt.y);
                Vector3 off = p - lookAt;
                c.Along = off.x * w.x + off.z * w.z;
                float far = new Vector2(off.x, off.z).magnitude;
                c.Envelope = StormCellRules.Envelope(c.Along, c.Age);
                c.InView = StormCellRules.InWedge(eye.x, eye.z, heading.x, heading.z, half + Mathf.Atan2(c.Radius, Mathf.Max(1f, far)), p.x, p.z);

                if (StormCellRules.Dead(c.Along, c.Age, far))
                {
                    Log.Msg("lightning: storm cell #" + c.Id + " ends -- " +
                            (c.Age >= StormCellRules.MaxAge ? "its time is up" :
                             far >= StormCellRules.LostAt ? "the view moved " + (far / 1000f).ToString("F1") + " km away" :
                             "passed, " + (c.Along / 1000f).ToString("F1") + " km downwind") +
                            " after " + (c.Age / 60f).ToString("F1") + " min");
                    _cells.RemoveAt(i);
                }
            }

            _birthCooldown -= simSeconds;
            if (_cells.Count >= StormCellRules.CellsWanted(activity) || _birthCooldown > 0f || simSeconds <= 0f)
                return;

            // A second cell does not come right on the heels of the first.
            _birthCooldown = _cells.Count == 0 ? 0f : 45f;

            float ground = Mathf.Max(_driftSpeed, StormCellRules.MinGroundSpeed);
            float distance = StormCellRules.BirthDistance(ground);
            Vector3 right = new Vector3(w.z, 0f, -w.x);

            for (int attempt = 0; attempt < 24; attempt++)
            {
                float d = distance * Mathf.Lerp(0.7f, 1.3f, (float)random.NextDouble());
                float side = Mathf.Lerp(-2500f, 2500f, (float)random.NextDouble());
                Vector3 spot = lookAt - w * d + right * side;
                if (cloudAt(spot) <= 0.6f)
                    continue;

                Cell cell = new Cell
                {
                    Id = _nextId++,
                    FrameX = spot.x - DriftX,
                    FrameZ = spot.z - DriftZ,
                    Radius = Mathf.Lerp(1200f, 3000f, (float)random.NextDouble()),
                    Steer = StormCellRules.Steer(w.x, w.z, _driftSpeed, side, d),
                };
                _cells.Add(cell);
                _loggedNoCloud = false;

                float speed = new Vector2(w.x * _driftSpeed + cell.Steer.x, w.z * _driftSpeed + cell.Steer.y).magnitude;
                Log.Msg("lightning: storm cell #" + cell.Id + " born " + (d / 1000f).ToString("F1") + " km upwind of the look-at point (" +
                        Bearing(-w) + "), " + (side / 1000f).ToString("+0.0;-0.0") + " km to the side, " +
                        (cell.Radius * 2f / 1000f).ToString("F1") + " km across, moving " + speed.ToString("F1") +
                        " m/s toward " + Bearing(w) + ", due in about " + (d / Mathf.Max(1f, speed) / 60f).ToString("F1") + " min");
                return;
            }

            if (!_loggedNoCloud)
            {
                _loggedNoCloud = true;
                Log.Msg("lightning: no storm cell -- no thick cloud upwind within " + (distance * 1.3f / 1000f).ToString("F0") +
                        " km; strikes stay around the camera until there is");
            }
            _birthCooldown = 10f;
        }

        /// <summary>A compass word for the log: the map's +z is north.</summary>
        public static string Bearing(Vector3 direction)
        {
            float degrees = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            if (degrees < 0f)
                degrees += 360f;
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return names[Mathf.RoundToInt(degrees / 45f) % 8] + " " + degrees.ToString("F0") + "deg";
        }

        public string Describe()
        {
            if (_cells.Count == 0)
                return "none";
            string text = "";
            foreach (Cell c in _cells)
            {
                text += (text.Length > 0 ? ", " : "") + "#" + c.Id + " " + (c.Along / 1000f).ToString("+0.0;-0.0") + " km along, x" +
                        c.Envelope.ToString("F2") + (c.InView ? " in view" : "");
            }
            return text;
        }
    }
}
