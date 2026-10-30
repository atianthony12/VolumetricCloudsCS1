using System;
using System.Globalization;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// The trees in the wind (pure; C# 5: tools/tree-bench.ps1 compiles this file as it is). The
    /// shaders are Trees*.shader (Trees.cginc's VWWindPoint); <see cref="TreeShaders"/> puts them on the
    /// game's tree materials and sets the globals from <see cref="Wind"/> once a frame.
    /// </summary>
    /// <remarks>
    /// The wind is in "breezes": 1 = a breezy fair day. It grows with the cloud cover, the rain and a
    /// storm (a waterspout about), each part scaled by the rows -- or it is the Now tab's override. The
    /// lean at the crown is <see cref="LeanPerBreeze"/> x speed^1.5 radians (2 deg at 1, 5.7 at 2, 10.4
    /// at 3); the gusts swing it by +- their strength where they pass, drifting downwind at 4 + 6 x speed
    /// m/s; each tree sways round its lean (<see cref="SwayShare"/>, x 0.5 in a lull to 1.5 in a gust) on
    /// three rates round its own; the branches swing <see cref="BranchPerBreeze"/> x speed^1.5 of the
    /// tree's size (at most <see cref="BranchMost"/>); the leaves 6 x speed^2 cm (at most 60). Each tree's
    /// own wind (the game's: higher ground more, Tree Control's factors in it) scales all of it.
    /// Starting points, tuned in-game.
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
        public const float SwayShare = 0.5f;
        public const float AcrossShare = 0.3f;
        public const float BranchPerBreeze = 0.012f;
        public const float BranchMost = 0.08f;
        public const float FlutterPerBreeze2 = 0.06f;
        public const float FlutterMost = 0.6f;

        /// <summary>The Now tab's wind strength: the slider's range in breezes.</summary>
        public const float OverrideMost = 4f;

        /// <summary>
        /// How quickly the trees follow a change of wind (s of simulation): the strength's time constant,
        /// and the direction's, which also turns at most <see cref="TurnMost"/> degrees a second -- the
        /// game's wind can swing round in a few seconds, and a whole forest swinging with it looks wrong.
        /// </summary>
        public const float EaseSeconds = 3f;
        public const float TurnSeconds = 8f;
        public const float TurnMost = 12f;

        /// <summary>The wind as it is now, for the shaders and the log.</summary>
        public struct Wind
        {
            public float Speed;      // breezes
            public float Fair;       // its parts, before Strength
            public float Cover;
            public float Rain;
            public float Storm;
            public bool Overridden;  // the Now tab's wind (in Fair; the other parts 0)
            public float Lean;       // radians at the crown, before each tree's own wind and the gusts
            public float Gusts;      // the gust strength, 0..0.9
            public float Branch;     // the branches' swing, share of the tree's size
            public float Flutter;    // m
            public float GustSpeed;  // m/s
        }

        /// <summary>
        /// The wind from the weather. <paramref name="strength"/>, <paramref name="gusts"/>,
        /// <paramref name="flutter"/>, <paramref name="storms"/>: the rows (1 = 100%); cover, rain 0..1;
        /// storm 0..1 (a waterspout about); <paramref name="overrideBreezes"/>: the Now tab's wind in
        /// breezes in place of the weather's, or negative for none (the rows still scale it).
        /// </summary>
        public static Wind Compute(float strength, float gusts, float flutter, float storms, float cover, float rain, float storm, float overrideBreezes)
        {
            Wind w = new Wind();
            if (overrideBreezes >= 0f)
            {
                w.Overridden = true;
                w.Fair = Math.Min(overrideBreezes, OverrideMost);
            }
            else
            {
                w.Fair = 0.45f;
                w.Cover = 0.35f * Clamp01(cover);
                w.Rain = Math.Max(0f, storms) * 0.9f * Clamp01(rain);
                w.Storm = Math.Max(0f, storms) * 0.8f * Clamp01(storm);
            }

            w.Speed = Math.Max(0f, strength) * (w.Fair + w.Cover + w.Rain + w.Storm);
            float s15 = (float)Math.Pow(w.Speed, 1.5);
            w.Lean = LeanPerBreeze * s15;
            w.Gusts = Math.Min(0.9f, Math.Max(0f, gusts) * (0.35f + 0.15f * Math.Min(w.Speed, 2f)));
            w.Branch = Math.Min(BranchMost, Math.Max(0f, flutter) * BranchPerBreeze * s15);
            w.Flutter = Math.Min(FlutterMost, Math.Max(0f, flutter) * FlutterPerBreeze2 * w.Speed * w.Speed);
            w.GustSpeed = 4f + 6f * w.Speed;
            return w;
        }

        /// <summary><paramref name="from"/> moved toward <paramref name="to"/> by <paramref name="share"/> (0..1), every number.</summary>
        public static Wind Ease(Wind from, Wind to, float share)
        {
            float t = Clamp01(share);
            Wind w = to;
            w.Speed = from.Speed + (to.Speed - from.Speed) * t;
            w.Lean = from.Lean + (to.Lean - from.Lean) * t;
            w.Gusts = from.Gusts + (to.Gusts - from.Gusts) * t;
            w.Branch = from.Branch + (to.Branch - from.Branch) * t;
            w.Flutter = from.Flutter + (to.Flutter - from.Flutter) * t;
            w.GustSpeed = from.GustSpeed + (to.GustSpeed - from.GustSpeed) * t;
            return w;
        }

        /// <summary>
        /// The direction (degrees, the game's convention) turned from <paramref name="from"/> toward
        /// <paramref name="to"/> over <paramref name="seconds"/> of simulation: most of the way in
        /// <see cref="TurnSeconds"/>, never faster than <see cref="TurnMost"/> degrees a second.
        /// </summary>
        public static float Turn(float from, float to, float seconds)
        {
            if (seconds <= 0f)
                return from;
            float diff = to - from;
            diff -= 360f * (float)Math.Floor((diff + 180f) / 360f);
            float turn = diff * (1f - (float)Math.Exp(-seconds / TurnSeconds));
            float most = TurnMost * seconds;
            float angle = from + Math.Max(-most, Math.Min(most, turn));
            return angle - 360f * (float)Math.Floor(angle / 360f);
        }

        /// <summary>"breezy", "windy"... for the log and the Now tab's readout (its keys: Readout.Wind.*).</summary>
        public static string Describe(float speed)
        {
            if (speed < 0.3f) return "calm";
            if (speed < 0.8f) return "light breeze";
            if (speed < 1.3f) return "breezy";
            if (speed < 2f) return "windy";
            if (speed < 3f) return "stormy";
            return "gale";
        }

        /// <summary>The Now tab's readout key for a wind of <paramref name="speed"/> breezes.</summary>
        public static string DescribeKey(float speed)
        {
            if (speed < 0.3f) return "Readout.Wind.Calm";
            if (speed < 0.8f) return "Readout.Wind.Light";
            if (speed < 1.3f) return "Readout.Wind.Breezy";
            if (speed < 2f) return "Readout.Wind.Windy";
            if (speed < 3f) return "Readout.Wind.Stormy";
            return "Readout.Wind.Gale";
        }

        /// <summary>One log line: the wind and its parts, as the shaders get it.</summary>
        public static string Line(Wind w, float strength, float directionDeg)
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string parts = w.Overridden
                ? string.Format(c, "the Now tab's wind {0:F2}", w.Fair)
                : string.Format(c, "fair {0:F2} + cover {1:F2} + rain {2:F2} + storm {3:F2}", w.Fair, w.Cover, w.Rain, w.Storm);
            return string.Format(c,
                "{0} ({1:F2} breezes = {2:F0}% x ({3})): lean {4:F1} deg at the crown, " +
                "gusts +-{5:F0}% rolling toward {6:F0} deg at {7:F0} m/s, sway +-{8:F0}% of the lean, branches {9:F1}% of a tree's size, leaves {10:F0} cm",
                Describe(w.Speed), w.Speed, strength * 100f, parts, w.Lean * 180.0 / Math.PI,
                w.Gusts * 100f, directionDeg, w.GustSpeed, SwayShare * 100f, w.Branch * 100f, w.Flutter * 100f);
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

            /// <summary>How many times the clock has wrapped, 0..1023: the flying leaves' lives go on counting across a wrap.</summary>
            public int Epoch;

            public void Advance(double seconds, float dirX, float dirZ, float gustSpeed)
            {
                if (seconds <= 0.0)
                    return;
                double tile = GustCell * GustTileCells;
                Epoch = (Epoch + (int)Math.Floor((Clock + seconds) / ClockPeriod)) & 1023;
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
            public float Period, Across, Branch;            // _VWTreeClock.xyz
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
            g.Branch = w.Branch;
            return g;
        }

        private static float Frac(float x)
        {
            return x - (float)Math.Floor(x);
        }

        private static float SinTurns(float turns)
        {
            return (float)Math.Sin(6.2831853f * Frac(turns));
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

        /// <summary>How much of the wind a tree of size <paramref name="H"/> shows: (H / 12 m)^0.75, 0.3 to 1.</summary>
        public static float SizeShare(float H)
        {
            return (float)Math.Pow(Math.Min(1f, Math.Max(0.3f, H * (1f / 12f))), 0.75);
        }

        private static float TrunkRate(float H)
        {
            return Math.Min(0.9f, Math.Max(0.2f, 1.2f / (float)Math.Sqrt(H)));
        }

        /// <summary>
        /// Every number VWWindPoint rounds to whole cycles for a tree of <paramref name="height"/>, before
        /// rounding: the bench sets apart a tree whose rate sits on a rounding edge (the GPU's rsqrt is
        /// not the CPU's, so it may round the other way: a different rate, not a different law).
        /// </summary>
        public static float[] RawRates(float height, float period)
        {
            float H = Math.Max(height, 1f);
            float f0 = TrunkRate(H);
            float raw = f0 * period;
            float c = Math.Max(1f, RoundEven(raw));
            float rawLeaf = Math.Min(3.6f, Math.Max(1.8f, f0 * 6f)) * period;
            float cl = Math.Max(1f, RoundEven(rawLeaf));
            return new[] { raw, c * 1.618f, c * 0.41f, c * 0.87f, c * 2.13f, c * 2.6f, c * 3.7f, c * 3.1f, rawLeaf, cl * 1.43f, cl * 1.19f };
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
            float k = Math.Min(2f, Math.Max(0f, speed)) * SizeShare(H);
            float gust = GustAt(g, placeX, placeZ);
            float lean = g.Lean * k;
            float mean = lean * Math.Max(0f, 1f + g.Gusts * (2f * gust - 1f));
            float stir = 0.5f + gust;

            float turnX = Frac(0.0113000f * placeX - 0.0148014f * placeZ), turnY = Frac(0.0084352f * placeZ + 0.0138465f * placeX);
            float phaseX = Frac(0.137f * placeX + 0.211f * placeZ + 1.7f * (float)Math.Sin(6.2831853f * turnX));
            float phaseY = Frac(0.173f * placeX - 0.119f * placeZ + 1.3f * (float)Math.Sin(6.2831853f * turnY));
            float f0 = TrunkRate(H);
            float c = Math.Max(1f, RoundEven(f0 * g.Period));
            float t = g.Clock;
            float sway = 0.6f * SinTurns(c * t + phaseX)
                       + 0.3f * SinTurns(RoundEven(c * 1.618f) * t + phaseY)
                       + 0.35f * SinTurns(Math.Max(1f, RoundEven(c * 0.41f)) * t + phaseX + phaseY);
            float across = 0.7f * SinTurns(Math.Max(1f, RoundEven(c * 0.87f)) * t + phaseY)
                         + 0.3f * SinTurns(RoundEven(c * 2.13f) * t + phaseX);

            float sideX = -g.DirZ, sideZ = g.DirX;
            float rx = px - bx, ry = py - by, rz = pz - bz;
            float h = Math.Max(ry, 0f);
            float along = mean + lean * g.SwayShare * stir * sway;
            float aside = lean * g.Across * stir * across;
            float bend = h * h / H;
            float mx = rx + (g.DirX * along + sideX * aside) * bend;
            float my = ry;
            float mz = rz + (g.DirZ * along + sideZ * aside) * bend;
            float keep = (float)Math.Sqrt((rx * rx + ry * ry + rz * rz) / Math.Max(mx * mx + my * my + mz * mz, 1e-8f));
            mx *= keep; my *= keep; mz *= keep;

            float limb = flutter * Clamp01(h / (0.3f * H)) * (0.4f + 0.6f * Clamp01((float)Math.Sqrt(rx * rx + rz * rz) / (0.3f * H)));
            float spot = rx * 0.23f + ry * 0.17f + rz * 0.29f;
            float b1 = SinTurns(RoundEven(c * 2.6f) * t + spot + phaseX);
            float b2 = SinTurns(RoundEven(c * 3.7f) * t + spot * 1.3f + phaseY);
            float b3 = SinTurns(RoundEven(c * 3.1f) * t - spot * 0.8f + phaseX + phaseY);
            float swing = g.Branch * H * k * stir * limb;
            float down = 0.3f + 0.5f * b1;
            mx += (g.DirX * down + sideX * 0.35f * b3) * swing;
            my += 0.45f * b2 * swing;
            mz += (g.DirZ * down + sideZ * 0.35f * b3) * swing;

            float cl = Math.Max(1f, RoundEven(Math.Min(3.6f, Math.Max(1.8f, f0 * 6f)) * g.Period));
            float leaf = rx * 0.61f + ry * 0.83f + rz * 0.47f;
            float l1 = SinTurns(cl * t + leaf + phaseX);
            float l2 = SinTurns(RoundEven(cl * 1.43f) * t + leaf * 0.7f + phaseY);
            float l3 = SinTurns(RoundEven(cl * 1.19f) * t - leaf * 1.3f + phaseX);
            float amount = g.Flutter * flutter * k * (0.4f + 0.8f * gust);
            float ahead = 0.25f + 0.35f * l2;
            mx += (nx * l1 + sideX * 0.7f * l2 + g.DirX * ahead) * amount;
            my += (ny * l1 + 0.5f * l3) * amount;
            mz += (nz * l1 + sideZ * 0.7f * l2 + g.DirZ * ahead) * amount;

            ox = bx + mx;
            oy = by + my;
            oz = bz + mz;
        }
    }
}
