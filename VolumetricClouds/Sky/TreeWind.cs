using System;
using System.Globalization;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The trees in the wind (pure; C# 5: tools/tree-bench.ps1 compiles this file as it is). The
    /// shaders are Trees*.shader (Trees.cginc's VWWindPoint); <see cref="TreeShaders"/> puts them on the
    /// game's tree materials and sets the globals from <see cref="State"/> once a frame.
    /// </summary>
    /// <remarks>
    /// The wind is in "breezes": 1 = a breezy fair day. It grows with the cloud cover, the rain and a
    /// storm (a waterspout about), each part scaled by the rows. The lean at the crown is
    /// <see cref="LeanPerBreeze"/> x speed^1.5 radians (2 deg at 1, 5.7 at 2, 10.4 at 3); the gusts swing
    /// it by +- their strength where they pass, drifting downwind at 4 + 6 x speed m/s; each tree sways
    /// round its lean (<see cref="SwayShare"/>) at a rate set by its size; the leaves flutter
    /// 3 x speed^2 cm (at most 30). Each tree's own wind (the game's: higher ground more, Tree Control's
    /// factors in it) scales all of it. Starting points, tuned in-game.
    /// The shader's per-point maths has its twin in <see cref="PointAt"/>: change one, change the other.
    /// </remarks>
    public static class TreeWind
    {
        /// <summary>The clock's period (s of simulation): every sway makes whole cycles in it, so its wrap never jumps.</summary>
        public const double ClockPeriod = 64.0;

        /// <summary>The gust field's cell (m) and its tile in cells (the shader's VW_GUST_TILE): it repeats every 20.48 km.</summary>
        public const double GustCell = 160.0;
        public const int GustTileCells = 128;

        public const float LeanPerBreeze = 0.035f;
        public const float SwayShare = 0.35f;
        public const float AcrossShare = 0.25f;
        public const float FlutterPerBreeze2 = 0.03f;
        public const float FlutterMost = 0.3f;

        /// <summary>The wind as it is now, for the shaders and the log.</summary>
        public struct Wind
        {
            public float Speed;      // breezes
            public float Fair;       // its parts, before Strength
            public float Cover;
            public float Rain;
            public float Storm;
            public float Lean;       // radians at the crown, before each tree's own wind and the gusts
            public float Gusts;      // the gust strength, 0..0.9
            public float Flutter;    // m
            public float GustSpeed;  // m/s
        }

        /// <summary>
        /// The wind from the weather. <paramref name="strength"/>, <paramref name="gusts"/>,
        /// <paramref name="flutter"/>, <paramref name="storms"/>: the rows (1 = 100%); cover, rain 0..1;
        /// storm 0..1 (a waterspout about).
        /// </summary>
        public static Wind Compute(float strength, float gusts, float flutter, float storms, float cover, float rain, float storm)
        {
            Wind w = new Wind();
            w.Fair = 0.45f;
            w.Cover = 0.35f * Clamp01(cover);
            w.Rain = Math.Max(0f, storms) * 0.9f * Clamp01(rain);
            w.Storm = Math.Max(0f, storms) * 0.8f * Clamp01(storm);
            w.Speed = Math.Max(0f, strength) * (w.Fair + w.Cover + w.Rain + w.Storm);
            w.Lean = LeanPerBreeze * (float)Math.Pow(w.Speed, 1.5);
            w.Gusts = Math.Min(0.9f, Math.Max(0f, gusts) * (0.35f + 0.15f * Math.Min(w.Speed, 2f)));
            w.Flutter = Math.Min(FlutterMost, Math.Max(0f, flutter) * FlutterPerBreeze2 * w.Speed * w.Speed);
            w.GustSpeed = 4f + 6f * w.Speed;
            return w;
        }

        /// <summary>"breezy", "windy"... for the log.</summary>
        public static string Describe(float speed)
        {
            if (speed < 0.3f) return "calm";
            if (speed < 0.8f) return "light breeze";
            if (speed < 1.3f) return "breezy";
            if (speed < 2f) return "windy";
            return "stormy";
        }

        /// <summary>One log line: the wind and its parts, as the shaders get it.</summary>
        public static string Line(Wind w, float strength, float directionDeg)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            return string.Format(c,
                "{0} ({1:F2} breezes = {2:F0}% x (fair {3:F2} + cover {4:F2} + rain {5:F2} + storm {6:F2})): lean {7:F1} deg at the crown, " +
                "gusts +-{8:F0}% rolling toward {9:F0} deg at {10:F0} m/s, sway +-{11:F0}% of the lean, flutter {12:F0} cm",
                Describe(w.Speed), w.Speed, strength * 100f, w.Fair, w.Cover, w.Rain, w.Storm, w.Lean * 180.0 / Math.PI,
                w.Gusts * 100f, directionDeg, w.GustSpeed, SwayShare * 100f, w.Flutter * 100f);
        }

        /// <summary>
        /// The clock and the gust field's drift (doubles: no shader sees a drift), advanced by
        /// <paramref name="seconds"/> of simulation; the drift moves the gusts along
        /// (<paramref name="dirX"/>, <paramref name="dirZ"/>), the way the wind blows.
        /// </summary>
        public struct Motion
        {
            public double Clock;
            public double GustX;
            public double GustZ;

            public void Advance(double seconds, float dirX, float dirZ, float gustSpeed)
            {
                if (seconds <= 0.0)
                    return;
                double tile = GustCell * GustTileCells;
                Clock = Repeat(Clock + seconds, ClockPeriod);
                GustX = Repeat(GustX + dirX * gustSpeed * seconds, tile);
                GustZ = Repeat(GustZ + dirZ * gustSpeed * seconds, tile);
            }

            /// <summary>The clock as the shaders get it: 0..1 over <see cref="ClockPeriod"/>.</summary>
            public float Phase
            {
                get { return (float)(Clock / ClockPeriod); }
            }
        }

        private static double Repeat(double v, double period)
        {
            double r = v - Math.Floor(v / period) * period;
            return r >= period ? 0.0 : r;
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        // ---- The shader's twin (Trees.cginc), float for float, for the bench ----

        /// <summary>The globals as the shaders get them (Trees.cginc's _VWTree*).</summary>
        public struct Globals
        {
            public float Lean, Gusts, Flutter;              // _VWTreeWind.yzw
            public float DirX, DirZ, Clock;                 // _VWTreeWindDir.xz, w
            public float GustX, GustZ, InvCell, SwayShare;  // _VWTreeGust
            public float Period, Across;                    // _VWTreeClock.xy
        }

        public static Globals GlobalsOf(Wind w, Motion m, float dirX, float dirZ)
        {
            Globals g = new Globals();
            g.Lean = w.Lean;
            g.Gusts = w.Gusts;
            g.Flutter = w.Flutter;
            g.DirX = dirX;
            g.DirZ = dirZ;
            g.Clock = m.Phase;
            g.GustX = (float)m.GustX;
            g.GustZ = (float)m.GustZ;
            g.InvCell = (float)(1.0 / GustCell);
            g.SwayShare = SwayShare;
            g.Period = (float)ClockPeriod;
            g.Across = AcrossShare;
            return g;
        }

        private static float Frac(float x)
        {
            return x - (float)Math.Floor(x);
        }

        /// <summary>pcg2d (Jarzynski & Olano 2020) of a whole, non-negative cell: VWHash, to the bit.</summary>
        private static float Hash(float x, float y)
        {
            uint vx = (uint)x, vy = (uint)y;
            unchecked
            {
                vx = vx * 1664525u + 1013904223u;
                vy = vy * 1664525u + 1013904223u;
                vx += vy * 1664525u;
                vy += vx * 1664525u;
                vx ^= vx >> 16;
                vy ^= vy >> 16;
                vx += vy * 1664525u;
                vx ^= vx >> 16;
            }

            return (vx >> 8) * (1f / 16777216f);
        }

        private static float ValueNoise(float px, float py, float tile)
        {
            float ix = (float)Math.Floor(px), iy = (float)Math.Floor(py);
            float fx = px - ix, fy = py - iy;
            float ux = fx * fx * (3f - 2f * fx), uy = fy * fy * (3f - 2f * fy);
            float x0 = ix - tile * (float)Math.Floor(ix / tile), y0 = iy - tile * (float)Math.Floor(iy / tile);
            float x1 = x0 + 1f, y1 = y0 + 1f;
            if (x1 >= tile) x1 -= tile;
            if (y1 >= tile) y1 -= tile;
            float a = Hash(x0, y0), b = Hash(x1, y0), c = Hash(x0, y1), d = Hash(x1, y1);
            float ab = a + (b - a) * ux, cd = c + (d - c) * ux;
            return ab + (cd - ab) * uy;
        }

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        /// <summary>The gust field's two octaves of noise at a place (the bench's check of VWGust's parts).</summary>
        public static void NoiseAt(Globals g, float x, float z, out float first, out float second)
        {
            float px = (x - g.GustX) * g.InvCell, pz = (z - g.GustZ) * g.InvCell;
            first = ValueNoise(px, pz, GustTileCells);
            second = ValueNoise(px * 2.5f, pz * 2.5f, GustTileCells * 2.5f);
        }

        /// <summary>The gusts at a place, 0..1 (VWGust).</summary>
        public static float GustAt(Globals g, float x, float z)
        {
            float px = (x - g.GustX) * g.InvCell, pz = (z - g.GustZ) * g.InvCell;
            float n = 0.65f * ValueNoise(px, pz, GustTileCells) + 0.35f * ValueNoise(px * 2.5f, pz * 2.5f, GustTileCells * 2.5f);
            return SmoothStep(0.3f, 0.75f, n);
        }

        private static float RoundEven(float v)
        {
            return (float)Math.Round(v, MidpointRounding.ToEven);
        }

        /// <summary>
        /// VWWindPoint: point (px, py, pz) of a tree whose base is (bx, by, bz), place its world x, z,
        /// height its size (m), speed its own wind, flutter the point's weight, (nx, ny, nz) its normal.
        /// Returns the moved point.
        /// </summary>
        public static void PointAt(Globals g, float px, float py, float pz, float bx, float by, float bz, float placeX, float placeZ,
                                   float height, float speed, float flutter, float nx, float ny, float nz,
                                   out float ox, out float oy, out float oz)
        {
            float H = Math.Max(height, 1f);
            float k = Math.Min(2f, Math.Max(0f, speed));
            float gust = GustAt(g, placeX, placeZ);
            float mean = g.Lean * k * Math.Max(0f, 1f + g.Gusts * (2f * gust - 1f));

            float turnX = Frac(0.0113000f * placeX - 0.0148014f * placeZ), turnY = Frac(0.0084352f * placeZ + 0.0138465f * placeX);
            float phaseX = Frac(0.137f * placeX + 0.211f * placeZ + 1.7f * (float)Math.Sin(6.2831853f * turnX));
            float phaseY = Frac(0.173f * placeX - 0.119f * placeZ + 1.3f * (float)Math.Sin(6.2831853f * turnY));
            float cycles = Math.Max(1f, RoundEven(Math.Min(1.4f, Math.Max(0.25f, 2.5f / (float)Math.Sqrt(H))) * g.Period));
            float a = 6.2831853f * Frac(cycles * g.Clock + phaseX);
            float sway = (float)Math.Sin(a) + 0.25f * (float)Math.Sin(2f * a + 1.3f);
            float across = (float)Math.Sin(6.2831853f * Frac(Math.Max(1f, RoundEven(cycles * 0.73f)) * g.Clock + phaseY));

            float rx = px - bx, ry = py - by, rz = pz - bz;
            float h = Math.Max(ry, 0f);
            float along = mean * (1f + g.SwayShare * sway);
            float side = mean * g.Across * across;
            float bend = h * h / H;
            float mx = rx + (g.DirX * along - g.DirZ * side) * bend;
            float my = ry;
            float mz = rz + (g.DirZ * along + g.DirX * side) * bend;
            float keep = (float)Math.Sqrt((rx * rx + ry * ry + rz * rz) / Math.Max(mx * mx + my * my + mz * mz, 1e-8f));
            mx *= keep; my *= keep; mz *= keep;

            float quick = cycles * 3f;
            float spot = (rx * 1.7f + ry * 2.3f + rz * 1.1f) * 0.15915494f;
            float f1 = (float)Math.Sin(6.2831853f * Frac(quick * g.Clock + spot + phaseX));
            float f2 = (float)Math.Sin(6.2831853f * Frac(RoundEven(quick * 1.37f) * g.Clock + spot * 0.7f + phaseY));
            float amount = g.Flutter * flutter * k * (0.6f + 0.8f * gust);
            mx += (nx * f1 + g.DirX * 0.5f * f2) * amount;
            my += (ny * f1) * amount;
            mz += (nz * f1 + g.DirZ * 0.5f * f2) * amount;

            ox = bx + mx;
            oy = by + my;
            oz = bz + mz;
        }
    }
}
