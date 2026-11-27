using System;

namespace VolumetricClouds.Sky
{
    /// <summary>
    /// CLOUD LIFECYCLES (2026-11-25, phase 1): clouds that form,
    /// hold and dissolve from their edges while they drift. Two reads of the SAME weather map at two offsets (layer 0 and
    /// layer 1), each layer's weight a triangle over a per-place phase P(x) + the clock (half a cycle apart), blended so the
    /// variance is kept. A layer's offset changes only at the moment its weight is 0, so nothing on screen jumps.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>THE CLOCK: <see cref="CyclesPerSecond"/>: row 1.0 (100%) = one full cycle per <see cref="SecondsPerCycleAt100"/>
    /// seconds of simulation time at 1x (his answer 2026-11-25: "let's go with your number", 8 minutes). The caller keeps the
    /// clock in doubles (invariant 13) and hands the shader only <see cref="ClockFraction"/> and <see cref="ClockCycle"/>.</item>
    /// <item>THE PHASE P(x): smooth value noise on a lattice of <c>cells</c> per weather repeat (whole cells per repeat, so it
    /// tiles with the map; <c>cells</c> scales with the map's size so a cell is the same number of metres at any pattern size).
    /// The noise is equalised (<see cref="Gain"/>) and scaled by <see cref="Span"/> cycles.</item>
    /// <item>THE OFFSETS: layer 0 reads the map at a point in [0, <see cref="LayerWindow"/>)^2 of a repeat, layer 1 at a point
    /// in [<see cref="LayerGap"/>, <see cref="LayerGap"/> + <see cref="LayerWindow"/>)^2. The two windows are apart on both
    /// axes (the decorrelation the variance-keeping blend needs), and a layer's offset depends on its own cycle index only,
    /// never on the other layer's, so it can change only when that layer's weight is 0. Offsets snap to the map's
    /// <c>snap</c> step (16 texels: the empty-sky skip's cells line up with them).</item>
    /// <item>THE CYCLE INDEX: the offsets hash <c>(cycle + floor(frac(t) + P + k/2)) mod CycleRepeat</c>, so the shader needs
    /// only <c>frac(t)</c> and <c>floor(t) mod CycleRepeat</c>, never t (invariant 13).</item>
    /// <item>THE BLEND: <see cref="Blend"/> = mean + sum w_k (A_k - mean) / sqrt(sum w_k^2) (Heitz and Neyret; the same as
    /// CloudCommon's FarBlend). <see cref="BlendScore"/> is the normal-score variant: the same weights over the map's normal
    /// scores, read back through <see cref="ValueTable"/> (the map's own quantiles, 61 entries, no sampler).</item>
    /// <item>ROW 0 = THE PLAIN MAP: <see cref="Active"/> is false, the look is the plain read (offset 0, weight 1) and
    /// <see cref="Blend"/> returns the read itself, bit for bit.</item>
    /// </list>
    /// Pure, C# 5 (tools/test-lifecycle.ps1 and the harness compile the SOURCE). No UnityEngine type; the hash is
    /// <c>CloudCommon.cginc</c>'s CellHash, float for float. Static fields so the harness can sweep them.
    /// </remarks>
    public static class CloudLifecycle
    {
        /// <summary>What these numbers draw for a seed: a new number with any change.</summary>
        public const int Generator = 1;

        /// <summary>Game seconds (1x) for one full cycle at 100%: "let's go with your number" (8 minutes, 2026-11-25).</summary>
        public const double SecondsPerCycleAt100 = 480.0;

        /// <summary>The offsets' hash repeats every this many cycles (whole cycles, so the shader's index wraps exactly).</summary>
        public const int CycleRepeat = 256;

        /// <summary>The smoothing width of the amount ramp (CloudDensityField.Softness): the cover tables solve with it.</summary>
        public const float Softness = 0.18f;

        // ---- the tunables (static so the harness can sweep them)

        /// <summary>The phase range in cycles across the phase noise's full range (0..1 after equalising).</summary>
        public static float Span = 2f;

        /// <summary>
        /// The equaliser: the raw value noise's own distribution (its empirical CDF at 33 evenly spaced values, measured by
        /// Measure.cs MakeEqualiser over 4 million places). Mapped through it, the phase of a place is uniform over a repeat,
        /// so at any moment every phase holds its share of the sky (the cover holds). A 33-entry uniform array, no sampler.
        /// </summary>
        public static float[] EqualiserTable = new float[]
        {
            0.00000f, 0.00202f, 0.00813f, 0.01833f, 0.03317f, 0.05301f,
            0.07720f, 0.10578f, 0.13846f, 0.17450f, 0.21327f, 0.25516f,
            0.30019f, 0.34668f, 0.39486f, 0.44361f, 0.49275f, 0.54157f,
            0.58993f, 0.63782f, 0.68474f, 0.73055f, 0.77399f, 0.81491f,
            0.85295f, 0.88735f, 0.91741f, 0.94269f, 0.96346f, 0.97941f,
            0.99096f, 0.99778f, 1.00000f
        };

        /// <summary>The equaliser's entries are the raw noise at (j / 32): the table's endpoints are 0 and 1.</summary>
        public const int EqualiserSteps = 32;

        /// <summary>Where layer 1's window starts, in repeats (layer 0's window starts at 0).</summary>
        public static float LayerGap = 0.5f;

        /// <summary>The width of each layer's window of offsets, in repeats (the windows are apart by LayerGap - LayerWindow).</summary>
        public static float LayerWindow = 0.25f;

        /// <summary>Texels of the map an offset snaps to (the empty-sky skip's cell). 0 = no snapping.</summary>
        public const int SnapTexels = 16;

        /// <summary>The offsets' snap step in repeats for a map <paramref name="mapSide"/> texels across.</summary>
        public static float SnapFor(int mapSide)
        {
            return mapSide > 0 ? (float)SnapTexels / mapSide : 0f;
        }

        /// <summary>The lattice cells per repeat for a map <paramref name="scale"/> (1 = 384 texels over 13.5 km; 4 = the Larger pattern).</summary>
        public static int CellsFor(int cellsPer13km, int scale)
        {
            return Math.Max(1, cellsPer13km) * Math.Max(1, scale);
        }

        // ---- the clock ------------------------------------------------------------------------------------------

        /// <summary>
        /// Cycles per second of simulation time for a row (0..1 = 0..100%): row / <see cref="SecondsPerCycleAt100"/>.
        /// Negative rows count as 0.
        /// </summary>
        public static double CyclesPerSecond(float row)
        {
            return row > 0f ? (double)row / SecondsPerCycleAt100 : 0.0;
        }

        /// <summary>True when the lifecycle runs at this row (row above 0). At 0 every look is the plain map.</summary>
        public static bool Active(float row)
        {
            return row > 0f;
        }

        /// <summary>frac(t), 0..1, for the shader's phase: the clock within the current cycle.</summary>
        public static float ClockFraction(double t)
        {
            double f = t - Math.Floor(t);
            if (double.IsNaN(f) || double.IsInfinity(f))
                return 0f;
            float r = (float)f;
            return r >= 1f ? 0f : (r < 0f ? 0f : r);
        }

        /// <summary>floor(t) mod <see cref="CycleRepeat"/>, 0..CycleRepeat-1: the cycle index the offsets hash.</summary>
        public static int ClockCycle(double t)
        {
            double f = Math.Floor(t);
            if (double.IsNaN(f) || double.IsInfinity(f))
                return 0;

            // The fraction is rounded to a float (ClockFraction); when it rounds up to 1 (a clock a hair below a whole
            // cycle), the cycle is the next one, so the pair (fraction, cycle) is always one time. Without this the offsets
            // ran one cycle behind for a moment at every whole cycle (2026-11-25, found by the whole-map gate: L1 0.17).
            if ((float)(t - f) >= 1f)
                f += 1.0;
            double m = f - CycleRepeat * Math.Floor(f / CycleRepeat);
            int r = (int)m;
            return r < 0 ? 0 : (r >= CycleRepeat ? 0 : r);
        }

        // ---- the clock's state (the one place it advances: CloudLighting.LateUpdate) ----------------------------------

        /// <summary>
        /// The lifecycle clock t, in cycles, kept in [0, <see cref="CycleRepeat"/>) (a whole number of repeats, so the wrap
        /// changes nothing: every offset depends on floor(t) mod CycleRepeat only). Saved in SkyState version 3.
        /// </summary>
        public static double Clock { get; private set; }

        /// <summary>The row the clock last saw (0..2). The simulation thread reads the published copy through Read.</summary>
        private static float _row;

        /// <summary>The row as the main thread last set it (CloudLighting's Advance).</summary>
        public static float Row
        {
            get { return _row; }
        }

        /// <summary>
        /// The cover is re-solved every this many sim seconds at 1x (2026-11-25, his "60 s" of the round 2 measurement): the
        /// thresholds ease linearly between two solves that are one interval apart. Measured: 60 s holds the cover within
        /// 0.007 (round 2), 300 s does not.
        /// </summary>
        public const double ResolveSeconds = 60.0;

        /// <summary>
        /// The re-solve interval in CYCLES of the clock (2026-11-25 freeze fix): ResolveSeconds at 100% = an eighth of a cycle, the
        /// same at every row. The blended map depends on the clock alone, never on the row (the row is only the clock's speed), so
        /// a grid in clock units keeps every solve when the slider moves; before, the grid followed the row and every slider step
        /// threw the solves away and re-made two inline (seconds on a 1536^2 map: his freeze). 0 at row 0: nothing is re-solved.
        /// </summary>
        public static double ResolveCycles(float row)
        {
            return Active(row) ? ResolveSeconds * CyclesPerSecond(1f) : 0.0;
        }

        /// <summary>
        /// The cover-solve grid: the clock is cut into this many solve intervals per repeat (CycleRepeat / ResolveCycles, rounded).
        /// Indices wrap with the clock, so the grid is the same at t and t + CycleRepeat.
        /// </summary>
        public static int ResolveCount(float row)
        {
            double step = ResolveCycles(row);
            return step <= 0.0 ? 0 : (int)Math.Max(1.0, Math.Round(CycleRepeat / step));
        }

        /// <summary>The grid index of a clock value: floor(t / step) mod the count (0 when the grid is empty).</summary>
        public static int ResolveIndex(double clock, float row)
        {
            int n = ResolveCount(row);
            if (n <= 0)
                return 0;
            double pos = clock / ResolveCycles(row);
            double m = Math.Floor(pos);
            m = m - n * Math.Floor(m / n);
            int r = (int)m;
            return r < 0 ? 0 : (r >= n ? 0 : r);
        }

        /// <summary>The fraction of the way from grid index k to k + 1 (0..1) at the clock.</summary>
        public static float ResolveFraction(double clock, float row)
        {
            double step = ResolveCycles(row);
            if (step <= 0.0)
                return 0f;
            double pos = clock / step;
            double f = pos - Math.Floor(pos);
            return f < 0.0 ? 0f : (f >= 1.0 ? 0f : (float)f);
        }

        /// <summary>The clock value of grid index k (where the solve for it is made).</summary>
        public static double ResolveClock(int k, float row)
        {
            return k * ResolveCycles(row);
        }

        /// <summary>
        /// The drawn map's blended histogram at one clock: for every texel of a side x side map, the blend of its two offset reads
        /// (the same look as the shader's near lookup, at the texel's centre) binned into 256 bins over 0..1 (clamped). Fills
        /// <paramref name="hist"/> (length 256) and returns the sample count. <paramref name="drawn"/> = the drawn values
        /// (as the GPU reads them), <paramref name="mean"/> = the drawn map's mean. Pure: the worker and the tests call it.
        /// </summary>
        public static long BlendHistogram(float[] drawn, int side, float mean, double clock, float row, int cells, int saltX, int saltY,
                                          int[] hist)
        {
            return BlendHistogram(drawn, side, mean, clock, row, cells, saltX, saltY, hist, 1);
        }

        /// <summary>
        /// The same over every <paramref name="stride"/>-th texel each way (2026-11-25 freeze fix: the worker samples a 1536^2 map
        /// at 384^2, StrideFor; a histogram of 147k samples holds the cover to well under a point).
        /// </summary>
        public static long BlendHistogram(float[] drawn, int side, float mean, double clock, float row, int cells, int saltX, int saltY,
                                          int[] hist, int stride)
        {
            float snap = SnapFor(side);
            float clockFrac = ClockFraction(clock);
            int cycle = ClockCycle(clock);
            if (stride < 1)
                stride = 1;
            for (int b = 0; b < hist.Length; b++)
                hist[b] = 0;
            long total = 0;
            for (int y = 0; y < side; y += stride)
            {
                for (int x = 0; x < side; x += stride)
                {
                    float u = (x + 0.5f) / side, v = (y + 0.5f) / side;
                    Look look = LookAt(u, v, clockFrac, cycle, cells, saltX, saltY, snap, Active(row));
                    float a0 = drawn[NearestIndex(u + look.U0, v + look.V0, side)];
                    float a1 = drawn[NearestIndex(u + look.U1, v + look.V1, side)];
                    float value = Blend(a0, a1, mean, look);
                    int bin = (int)Math.Floor(Clamp01(value) * hist.Length);
                    if (bin >= hist.Length)
                        bin = hist.Length - 1;
                    hist[bin]++;
                    total++;
                }
            }
            return total;
        }

        /// <summary>The histogram stride for a map side: 384^2 samples whatever the map (1 at 384, 4 at 1536).</summary>
        public static int StrideFor(int side)
        {
            return Math.Max(1, side / 384);
        }

        /// <summary>The texel index of a point of the drifting frame (nearest, wrapped), as the field's twin reads it.</summary>
        public static int NearestIndex(float u, float v, int side)
        {
            int x = (int)Math.Floor(u * side) % side;
            int y = (int)Math.Floor(v * side) % side;
            if (x < 0)
                x += side;
            if (y < 0)
                y += side;
            return y * side + x;
        }


        private static readonly object SnapLock = new object();
        private static float _snapRow;
        private static double _snapClock;

        /// <summary>
        /// The row and the clock as the simulation thread reads them (CloudRain.LocalRain, SampleCloud). Published under a lock
        /// by Advance and Restore on the main thread, so the sim thread never sees half of an update and never reads the statics.
        /// </summary>
        public static void Read(out float row, out double clock)
        {
            lock (SnapLock)
            {
                row = _snapRow;
                clock = _snapClock;
            }
        }

        private static void Publish()
        {
            lock (SnapLock)
            {
                _snapRow = _row;
                _snapClock = Clock;
            }
        }

        /// <summary>Advances the clock by the sim time (real seconds x the sim speed, 0 while paused) at the row's rate.</summary>
        public static void Advance(float row, double simulationSeconds)
        {
            _row = row;
            if (Active(row) && !double.IsNaN(simulationSeconds) && !double.IsInfinity(simulationSeconds) && simulationSeconds > 0.0)
                Clock = WrapClock(Clock + CyclesPerSecond(row) * simulationSeconds);
            Publish();
        }

        /// <summary>A city's clock from its save (0 for a city without one, or a version-1/2 record).</summary>
        public static void Restore(double t)
        {
            Clock = WrapClock(double.IsNaN(t) || double.IsInfinity(t) ? 0.0 : t);
            Jumps++;
            Publish();
        }

        /// <summary>Lattice cells per 13.5 km of the phase field (2, the decision of 2026-11-25; the cells per repeat scale with the map).</summary>
        public static int CellsPer13km = 2;

        /// <summary>The phase field's two salts for a map seed: the harness's and the shader's, seed % 31 and (seed / 31) % 31.</summary>
        public static void SaltsFor(int seed, out int saltX, out int saltY)
        {
            int s = seed % 31;
            if (s < 0)
                s += 31;
            int q = (seed / 31) % 31;
            if (q < 0)
                q += 31;
            saltX = s;
            saltY = q;
        }

        /// <summary>
        /// The C# twin's blend of the two offset reads, with the look of one place (the shader's near lookup under VC_PLUS).
        /// <paramref name="drawn0"/>, <paramref name="drawn1"/> = the drawn map at (u + look.U0, v + look.V0) and
        /// (u + look.U1, v + look.V1); <paramref name="mean"/> = the drawn map's mean. Row 0 returns <paramref name="drawn0"/>.
        /// </summary>
        public static float TwinValue(float drawn0, float drawn1, float mean, float u, float v, float clockFrac, int cycleMod,
                                      int cells, int saltX, int saltY, float snap, bool active, out Look look)
        {
            look = LookAt(u, v, clockFrac, cycleMod, cells, saltX, saltY, snap, active);
            return Blend(drawn0, drawn1, mean, look);
        }

        /// <summary>t wrapped into [0, CycleRepeat).</summary>
        public static double WrapClock(double t)
        {
            double m = t - CycleRepeat * Math.Floor(t / CycleRepeat);
            return m < 0.0 || m >= CycleRepeat ? 0.0 : m;
        }

        // ---- the phase P(x) ----------------------------------------------------------------------------------------

        /// <summary>
        /// The phase of a place, in cycles (0..Span): smooth value noise over <paramref name="cells"/> lattice cells per
        /// repeat, equalised by <see cref="Gain"/>. <paramref name="u"/>, <paramref name="v"/> = the place in the
        /// drifting frame (xz / tile - phase, in repeats); <paramref name="saltX"/>, <paramref name="saltY"/> = the map's
        /// seed as whole-cell shifts of the lattice (a tiling shift: the lattice still repeats every cell count).
        /// Periodic in u and v with period 1 (tests/test-lifecycle.ps1).
        /// </summary>
        public static float Phase(float u, float v, int cells, int saltX, int saltY)
        {
            if (PerCellPhase)
            {
                // EXPERIMENT (2026-11-25, round 2, off by default): one constant phase per lattice cell, no noise, no blend.
                // The cell's value is a golden-ratio sequence over the cell index (equidistributed over a repeat).
                int ix = (int)Math.Floor(u * cells), iy = (int)Math.Floor(v * cells);
                ix = ((ix % cells) + cells) % cells;
                iy = ((iy % cells) + cells) % cells;
                float k = (float)(ix + iy * cells);
                float off = Frac(0.5f + 0.6180339887f * saltX + 0.7548776662f * saltY);
                return Span * Frac(k * 0.6180339887f + off);
            }

            return Span * Equalise(Noise(u, v, cells, saltX, saltY));
        }

        /// <summary>EXPERIMENT (round 2): a constant phase per lattice cell instead of the equalised noise. Default false.</summary>
        public static bool PerCellPhase = false;

        /// <summary>The raw value noise, 0..1 (before the equaliser): what <see cref="Phase"/> equalises.</summary>
        public static float Noise(float u, float v, int cells, int saltX, int saltY)
        {
            if (cells < 1)
                cells = 1;

            float x = u * cells;
            float y = v * cells;
            float fx0 = (float)Math.Floor(x);
            float fy0 = (float)Math.Floor(y);
            float tx = x - fx0;
            float ty = y - fy0;
            float sx = tx * tx * (3f - 2f * tx);
            float sy = ty * ty * (3f - 2f * ty);

            float c = (float)cells;
            float h00 = Hash(fx0, fy0, c, saltX, saltY);
            float h10 = Hash(fx0 + 1f, fy0, c, saltX, saltY);
            float h01 = Hash(fx0, fy0 + 1f, c, saltX, saltY);
            float h11 = Hash(fx0 + 1f, fy0 + 1f, c, saltX, saltY);

            float a = h00 + (h10 - h00) * sx;
            float b = h01 + (h11 - h01) * sx;
            return a + (b - a) * sy;
        }

        /// <summary>The raw noise through <see cref="EqualiserTable"/> (linear between its entries): uniform over 0..1 when the noise is.</summary>
        public static float Equalise(float value)
        {
            float t = Clamp01(value) * EqualiserSteps;
            int i = (int)t;
            if (i >= EqualiserSteps)
                return EqualiserTable[EqualiserSteps];
            float f = t - i;
            return EqualiserTable[i] + (EqualiserTable[i + 1] - EqualiserTable[i]) * f;
        }

        // ---- the layers' look ------------------------------------------------------------------------------------------

        /// <summary>
        /// Where a place reads the map: the two offsets (in repeats, snapped) and the two layer weights. <see cref="Plain"/> =
        /// the lifecycle is off here (row 0): read the map at offset 0 and use it as it is.
        /// </summary>
        public struct Look
        {
            public float W0, W1;
            public float U0, V0, U1, V1;
            public bool Plain;
        }

        /// <summary>
        /// The look at a place: mirrors the shader's form (one call per read of the weather map). <paramref name="clockFrac"/>
        /// = frac(t), <paramref name="cycleMod"/> = floor(t) mod CycleRepeat, <paramref name="snap"/> = the offsets' snap
        /// step in repeats (<see cref="SnapFor"/>). Row 0 (<paramref name="active"/> false) returns the plain look.
        /// </summary>
        public static Look LookAt(float u, float v, float clockFrac, int cycleMod, int cells, int saltX, int saltY, float snap, bool active)
        {
            Look look;
            if (!active)
            {
                look.W0 = 1f;
                look.W1 = 0f;
                look.U0 = look.V0 = look.U1 = look.V1 = 0f;
                look.Plain = true;
                return look;
            }

            float p = Phase(u, v, cells, saltX, saltY);

            // Layer 0: phi = frac(clock + P); its cycle index is floor(clock + P).
            float s0 = clockFrac + p;
            float phi0 = s0 - (float)Math.Floor(s0);
            float w0 = 1f - Math.Abs(2f * phi0 - 1f);
            int n0 = cycleMod + (int)Math.Floor(s0);

            // Layer 1: half a cycle later.
            float s1 = clockFrac + p + 0.5f;
            float phi1 = s1 - (float)Math.Floor(s1);
            float w1 = 1f - Math.Abs(2f * phi1 - 1f);
            int n1 = cycleMod + (int)Math.Floor(s1);

            Vector2Like o0 = Offset(n0, 0, 0f, LayerWindow, snap);
            Vector2Like o1 = Offset(n1, 1, LayerGap, LayerWindow, snap);

            look.W0 = w0;
            look.W1 = w1;
            look.U0 = o0.X;
            look.V0 = o0.Y;
            look.U1 = o1.X;
            look.V1 = o1.Y;
            look.Plain = false;
            return look;
        }

        /// <summary>The variance-keeping blend of two reads (the layers' weights in <paramref name="look"/>): mean + sum w (a - mean) / sqrt(sum w^2).</summary>
        public static float Blend(float a0, float a1, float mean, Look look)
        {
            if (look.Plain)
                return a0;

            float norm = (float)Math.Sqrt(look.W0 * look.W0 + look.W1 * look.W1);
            return mean + (look.W0 * (a0 - mean) + look.W1 * (a1 - mean)) / norm;
        }

        /// <summary>
        /// The share of a cadence's limit that the lifecycle used between two clock readings (2026-11-25; for ShadowCadence
        /// and ColumnCadence, which add it to their other shares: a read is due when the shares sum to 1). The field changes in
        /// place by the clock: the wrapped step from <paramref name="from"/> to <paramref name="to"/> (cycles), against
        /// <see cref="CycleLimit"/>. 0 at row 0 (the plain map never changes) and before a first reading (NaN).
        /// </summary>
        public static double ChangeShare(double from, double to, float row)
        {
            return ChangeShare(from, to, row, CycleLimit);
        }

        /// <summary>
        /// The same against another limit (2026-11-25: the cloud column map's readback costs ~69 ms a read, so its limit is
        /// <see cref="ColumnCycleLimit"/>, not the shadow map's <see cref="CycleLimit"/>).
        /// </summary>
        public static double ChangeShare(double from, double to, float row, double limit)
        {
            if (!Active(row) || double.IsNaN(from) || double.IsNaN(to) || double.IsInfinity(from) || double.IsInfinity(to) || limit <= 0.0)
                return 0.0;
            return Math.Abs(ClockDelta(from, to)) / limit;
        }

        /// <summary>
        /// The column map's lifecycle limit, in cycles (0.06 = about 29 s at 100%: the wind's own cadence; the shadow map keeps
        /// <see cref="CycleLimit"/>). A GUESS from the 2026-11-20 aged-map numbers; the strike tops must be measured in the bench.
        /// </summary>
        public const double ColumnCycleLimit = 0.06;

        /// <summary>
        /// How many cycles the field may move between two reads before a cadence must read again. A GUESS, not measured:
        /// 0.01 cycle = 4.8 s at 100%. Tune with the bench (the column map and the shadow map's redraw counts).
        /// </summary>
        public const double CycleLimit = 0.01;

        /// <summary>The largest blended value a skip cell can hold, whatever the cell's weights are (2026-11-25, for the empty-sky
        /// skip). The two weights always sum to 1 (w1 = 1 - w0), so (w0, w1) / norm is a unit vector on the quarter
        /// circle, and the blend is mean + A cos t + B sin t for t in [0, pi/2], A = max0 - mean, B = max1 - mean, where
        /// max_k = the largest map value of the cell read at layer k's offset. That maximum is sqrt(A^2 + B^2) when both
        /// are at least 0 (the peak lies on the arc) and max(A, B) otherwise (the peak lies off it). A cell is skipped only
        /// when this bound is at or below the threshold: the bound holds for every weight the cell can have.
        /// </summary>
        public static float SkipBound(float mean, float max0, float max1)
        {
            double a = max0 - mean, b = max1 - mean;
            double extra = a >= 0.0 && b >= 0.0 ? Math.Sqrt(a * a + b * b) : Math.Max(a, b);
            return mean + (float)extra;
        }

        // ---- the heaps' line in the blended scores (2026-11-25, task 1) ---------------------------------------------------

        /// <summary>The score histograms' bins: 0.01 of normal score over -<see cref="ScoreRange"/>..+<see cref="ScoreRange"/>.</summary>
        public const int ScoreBins = 600;

        /// <summary>The width of one score bin.</summary>
        public const float ScoreBinWidth = 0.01f;

        /// <summary>The bin of a normal score (clamped to the histogram's ends).</summary>
        public static int ScoreBin(float z)
        {
            int b = (int)Math.Floor((Clamp(z, -ScoreRange, ScoreRange) + ScoreRange) / ScoreBinWidth);
            return b < 0 ? 0 : (b >= ScoreBins ? ScoreBins - 1 : b);
        }

        /// <summary>The normal score a texel holds: the stored byte, (v / 255) x 2 x ScoreRange - ScoreRange (CloudDensityField.Scores).</summary>
        public static float ScoreOfByte(byte v)
        {
            return v / 255f * (2f * ScoreRange) - ScoreRange;
        }

        /// <summary>
        /// The drawn map's blended SCORE histogram at one clock (the heaps' view): for every texel of a side x side map, the
        /// blend of its two offset reads of the normal scores (the same weights as the value blend, <see cref="BlendScore"/>),
        /// binned into <see cref="ScoreBins"/>. <paramref name="scores"/> = the stored score bytes. Fills <paramref name="hist"/>
        /// and returns the sample count.
        /// </summary>
        public static long BlendScoreHistogram(byte[] scores, int side, double clock, float row, int cells, int saltX, int saltY, int[] hist)
        {
            return BlendScoreHistogram(scores, side, clock, row, cells, saltX, saltY, hist, 1);
        }

        /// <summary>The same over every <paramref name="stride"/>-th texel each way (see BlendHistogram).</summary>
        public static long BlendScoreHistogram(byte[] scores, int side, double clock, float row, int cells, int saltX, int saltY, int[] hist,
                                               int stride)
        {
            float snap = SnapFor(side);
            float clockFrac = ClockFraction(clock);
            int cycle = ClockCycle(clock);
            if (stride < 1)
                stride = 1;
            for (int b = 0; b < hist.Length; b++)
                hist[b] = 0;
            long total = 0;
            for (int y = 0; y < side; y += stride)
            {
                for (int x = 0; x < side; x += stride)
                {
                    float u = (x + 0.5f) / side, v = (y + 0.5f) / side;
                    Look look = LookAt(u, v, clockFrac, cycle, cells, saltX, saltY, snap, Active(row));
                    float z0 = ScoreOfByte(scores[NearestIndex(u + look.U0, v + look.V0, side)]);
                    float z1 = ScoreOfByte(scores[NearestIndex(u + look.U1, v + look.V1, side)]);
                    hist[ScoreBin(BlendScore(z0, z1, look))]++;
                    total++;
                }
            }
            return total;
        }

        /// <summary>The share of a score histogram lying at or above <paramref name="z"/> (whole bins).</summary>
        public static float ShareAbove(int[] hist, long total, float z)
        {
            if (total <= 0)
                return 0f;
            long sum = 0;
            for (int b = ScoreBin(z); b < ScoreBins; b++)
                sum += hist[b];
            return (float)sum / total;
        }

        /// <summary>
        /// The score z whose share above it is <paramref name="share"/> (the lower edge of the bin where the share from the top
        /// first reaches it). Solves the heaps' line in the blended scores: the edge whose cover matches the plain map's.
        /// </summary>
        public static float ZAtShare(int[] hist, long total, float share)
        {
            if (total <= 0 || share <= 0f)
                return ScoreRange;
            double need = share * total;
            double cum = 0.0;
            for (int b = ScoreBins - 1; b >= 0; b--)
            {
                cum += hist[b];
                if (cum >= need)
                    return -ScoreRange + b * ScoreBinWidth;
            }
            return -ScoreRange;
        }

        /// <summary>
        /// The wrapped clock step from <paramref name="from"/> to <paramref name="to"/>, in cycles: the cycle index wraps at
        /// <see cref="CycleRepeat"/>, so a step across the wrap is a small step, not 255 cycles.
        /// </summary>
        public static double ClockDelta(double from, double to)
        {
            double d = to - from;
            return d - CycleRepeat * Math.Round(d / CycleRepeat);
        }

        /// <summary>
        /// Counts the clock's jumps (<see cref="Restore"/>: a city's load, or a save's clock): a cadence that reads
        /// it treats a jump as an input change, not as a share of its limit.
        /// </summary>
        public static long Jumps { get; private set; }

        /// <summary>The same blend over normal scores (mean 0): the score of the blended read, which <see cref="ValueTable"/> reads back.</summary>
        public static float BlendScore(float z0, float z1, Look look)
        {
            if (look.Plain)
                return z0;

            float norm = (float)Math.Sqrt(look.W0 * look.W0 + look.W1 * look.W1);
            return (look.W0 * z0 + look.W1 * z1) / norm;
        }

        // ---- the normal-score variant's tables -------------------------------------------------------------------------

        /// <summary>The score table's range: standard normal scores -<see cref="ScoreRange"/>..+<see cref="ScoreRange"/> (as ScoreTexture).</summary>
        public const float ScoreRange = 3f;

        /// <summary>The score table's entries: one per 0.1 of score over -3..3.</summary>
        public const int ValueEntries = 61;

        /// <summary>
        /// The normal score of each value of a map (rank-based: z = NormalQuantile((rank + 0.5) / n)), in the same order as
        /// <paramref name="values"/>. Ties share the mean rank. Pure: a sort and a pass.
        /// </summary>
        public static float[] ScoresOf(float[] values)
        {
            int n = values.Length;
            int[] order = new int[n];
            for (int i = 0; i < n; i++)
                order[i] = i;
            Array.Sort(order, (a, b) => values[a].CompareTo(values[b]));

            float[] z = new float[n];
            int start = 0;
            while (start < n)
            {
                int end = start;
                while (end + 1 < n && values[order[end + 1]] == values[order[start]])
                    end++;

                // Ties share the middle of their ranks.
                float rank = (start + end + 1) * 0.5f;
                float zz = Clamp(NormalQuantile(rank / n), -ScoreRange, ScoreRange);
                for (int i = start; i <= end; i++)
                    z[order[i]] = zz;
                start = end + 1;
            }

            return z;
        }

        /// <summary>
        /// The map's own quantiles at the scores of <see cref="ValueEntries"/> evenly spaced points over -3..3: entry j = the
        /// value at the share Phi(z_j) of the sorted map. Read by <see cref="ValueAt"/> (interpolated).
        /// </summary>
        public static float[] ValueTable(float[] values)
        {
            float[] sorted = (float[])values.Clone();
            Array.Sort(sorted);
            int n = sorted.Length;

            float[] table = new float[ValueEntries];
            for (int j = 0; j < ValueEntries; j++)
            {
                float z = -ScoreRange + j * (2f * ScoreRange / (ValueEntries - 1));
                double p = Phi(z);
                int idx = (int)Math.Floor(p * n);
                if (idx < 0)
                    idx = 0;
                if (idx > n - 1)
                    idx = n - 1;
                table[j] = sorted[idx];
            }

            return table;
        }

        /// <summary>The map's value at a blended score: the table, interpolated, clamped to its ends.</summary>
        public static float ValueAt(float[] table, float z)
        {
            float t = (Clamp(z, -ScoreRange, ScoreRange) + ScoreRange) / (2f * ScoreRange) * (ValueEntries - 1);
            int i = (int)Math.Floor(t);
            if (i >= ValueEntries - 1)
                return table[ValueEntries - 1];
            if (i < 0)
                return table[0];
            float f = t - i;
            return table[i] + (table[i + 1] - table[i]) * f;
        }

        // ---- the cover table (one for the mixture) --------------------------------------------------------------------

        /// <summary>
        /// The threshold that gives the sky's mean amount <paramref name="coverage"/> (0..1) for a histogram of the values a
        /// place can read, the same solve as CloudDensityField (softness <see cref="Softness"/>). Table of 33 steps, lerped.
        /// </summary>
        public sealed class CoverTable
        {
            public const int Steps = 32;
            public const int Bins = 256;

            private readonly float[] _table = new float[Steps + 1];

            /// <summary>Builds the table from a histogram of values (clamped 0..1) with <paramref name="total"/> samples.</summary>
            public static CoverTable FromHistogram(int[] histogram, long total)
            {
                CoverTable t = new CoverTable();
                for (int step = 0; step <= Steps; step++)
                    t._table[step] = Solve(histogram, total, step / (float)Steps, Softness);
                return t;
            }

            /// <summary>
            /// THE CLOUDS' TABLE UNDER THE LIFECYCLE (2026-11-25 fix: "it's overriding the intensity"). The clouds' plain
            /// threshold is solved against the STORED densities while the GPU compares decoded ones, so the plain sky covers far
            /// less than its slider says -- the look the clouds were tuned to. A blended table solved for the slider's share
            /// therefore drew far MORE cloud than the plain sky. This one keeps the plain sky's real cover: for each step, the
            /// share the plain drawn map really covers at the plain threshold (<paramref name="plainTable"/>, the field's
            /// stored-space table, any length >= 2), then the blended histogram's threshold for that share.
            /// </summary>
            public static CoverTable MatchingPlain(int[] blended, long blendedTotal, int[] plainDrawn, long plainDrawnTotal, float[] plainTable)
            {
                CoverTable t = new CoverTable();
                for (int step = 0; step <= Steps; step++)
                {
                    float plainThreshold = Lerped(plainTable, step / (float)Steps);
                    float share = MeanAmount(plainDrawn, plainDrawnTotal, plainThreshold, Softness);
                    t._table[step] = Solve(blended, blendedTotal, share, Softness);
                }
                return t;
            }

            /// <summary>A table of thresholds over coverage 0..1 (first = 0, last = 1), lerped at <paramref name="coverage"/>.</summary>
            public static float Lerped(float[] table, float coverage)
            {
                int last = table.Length - 1;
                float t = Clamp01(coverage) * last;
                int index = (int)t;
                if (index > last - 1)
                    index = last - 1;
                if (index < 0)
                    index = 0;
                return table[index] + (table[index + 1] - table[index]) * (t - index);
            }

            /// <summary>The mean amount a histogram of values (Bins bins) gives at a threshold: the share of the sky it covers.</summary>
            public static float MeanAmount(int[] histogram, long total, float threshold, float softness)
            {
                if (total <= 0)
                    return 0f;
                double sum = 0.0;
                for (int bin = 0; bin < Bins; bin++)
                {
                    if (histogram[bin] != 0)
                        sum += Clamp01(((bin + 0.5f) / Bins - threshold) / softness) * histogram[bin];
                }
                return (float)(sum / total);
            }

            /// <summary>Builds the table from values in 0..1.</summary>
            public static CoverTable FromValues(float[] values)
            {
                int[] hist = new int[Bins];
                for (int i = 0; i < values.Length; i++)
                {
                    int bin = (int)Math.Floor(Clamp01(values[i]) * Bins);
                    if (bin >= Bins)
                        bin = Bins - 1;
                    hist[bin]++;
                }
                return FromHistogram(hist, values.Length);
            }

            /// <summary>The threshold for a coverage 0..1: lerped from the table.</summary>
            public float GetThreshold(float coverage)
            {
                float t = Clamp01(coverage) * Steps;
                int index = (int)t;
                if (index > Steps - 1)
                    index = Steps - 1;
                if (index < 0)
                    index = 0;
                return _table[index] + (_table[index + 1] - _table[index]) * (t - index);
            }

            public static float Solve(int[] histogram, long total, float target, float softness)
            {
                if (target <= 0f)
                    return 1f + softness;
                if (target >= 1f)
                    return -softness;

                float low = -softness;
                float high = 1f + softness;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (low + high) * 0.5f;
                    double sum = 0.0;
                    for (int bin = 0; bin < Bins; bin++)
                    {
                        if (histogram[bin] != 0)
                            sum += Clamp01(((bin + 0.5f) / Bins - mid) / softness) * histogram[bin];
                    }

                    // The mean amount falls as the threshold rises.
                    if (sum / total > target)
                        low = mid;
                    else
                        high = mid;
                }

                return (low + high) * 0.5f;
            }
        }

        /// <summary>The amount a read of <paramref name="value"/> gives at threshold <paramref name="threshold"/> (the shaders' saturate((v - t) / softness)).</summary>
        public static float Amount(float value, float threshold)
        {
            return Clamp01((value - threshold) / Softness);
        }

        // ---- the hash and the helpers -------------------------------------------------------------------------------

        /// <summary>
        /// The offset of one layer's cycle <paramref name="n"/>: two numbers in a window of the repeat, snapped. The hash is
        /// CloudCommon's CellHash on (n, layer) with the cycle repeat, so n and n + CycleRepeat read the same offset.
        /// </summary>
        private static Vector2Like Offset(int n, int layer, float start, float window, float snap)
        {
            float[] h = CellHashXY((float)n, (float)layer, (float)CycleRepeat);
            float x = start + h[0] * window;
            float y = start + h[1] * window;
            if (snap > 0f)
            {
                x = (float)Math.Floor(x / snap) * snap;
                y = (float)Math.Floor(y / snap) * snap;
            }

            Vector2Like r;
            r.X = x;
            r.Y = y;
            return r;
        }

        /// <summary>A hash of a lattice cell (whole cells, periodic in <paramref name="repeat"/>): CloudCommon's CellHash, returning its x and y.</summary>
        private static float Hash(float x, float y, float repeat, int saltX, int saltY)
        {
            // The cell wraps to the lattice first (so the noise tiles with the map), then the map's seed enters the hash
            // itself: a shift of the lattice would give every city the same values, moved (measured 2026-11-25).
            float xm = x - repeat * (float)Math.Floor(x / repeat);
            float ym = y - repeat * (float)Math.Floor(y / repeat);
            float[] h = CellHashXY(xm + 64f * saltX, ym + 64f * saltY, SaltRepeat);
            return h[0];
        }

        /// <summary>The seeded hash's own repeat (whole ids: the seed's 64-step offsets stay inside it for 31 x 31 seeds).</summary>
        private const float SaltRepeat = 4096f;

        /// <summary>
        /// CloudCommon.cginc's CellHash (Hoskins' hash, no sines), float for float: three numbers 0..1 for the cell (x, y) of a
        /// lattice that repeats every <paramref name="repeat"/> cells. Returned as an array of the first two.
        /// </summary>
        private static float[] CellHashXY(float x, float y, float repeat)
        {
            x -= repeat * (float)Math.Floor(x / repeat);
            y -= repeat * (float)Math.Floor(y / repeat);

            // float3(id.xyx) * float3(0.1031, 0.1030, 0.0973) + 0.37, then frac.
            float p0 = Frac(x * 0.1031f + 0.37f);
            float p1 = Frac(y * 0.1030f + 0.37f);
            float p2 = Frac(x * 0.0973f + 0.37f);

            // p3 += dot(p3, p3.yxz + 33.33)
            float d = p0 * (p1 + 33.33f) + p1 * (p0 + 33.33f) + p2 * (p2 + 33.33f);
            p0 += d;
            p1 += d;
            p2 += d;

            // frac((p3.xxy + p3.yzz) * p3.zyx)
            float r0 = Frac((p0 + p1) * p2);
            float r1 = Frac((p0 + p2) * p1);
            return new float[] { r0, r1 };
        }

        private static float Frac(float v)
        {
            return v - (float)Math.Floor(v);
        }

        /// <summary>The standard normal CDF (Abramowitz and Stegun 7.1.26 erf, error under 1.5e-7).</summary>
        public static double Phi(double z)
        {
            double x = z / Math.Sqrt(2.0);
            double sign = x < 0 ? -1.0 : 1.0;
            x = Math.Abs(x);
            double t = 1.0 / (1.0 + 0.3275911 * x);
            double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
            return 0.5 * (1.0 + sign * y);
        }

        /// <summary>The standard normal quantile (Acklam's rational approximation, relative error under 1.2e-9; as CloudDensityField.NormalQuantile).</summary>
        public static float NormalQuantile(float p)
        {
            double q, r;
            const double low = 0.02425, high = 1 - 0.02425;

            if (p <= 0f)
                return float.NegativeInfinity;
            if (p >= 1f)
                return float.PositiveInfinity;

            if (p < low)
            {
                q = Math.Sqrt(-2 * Math.Log(p));
                return (float)((((((-7.784894002430293e-03 * q - 3.223964580411365e-01) * q - 2.400758277161838e+00) * q - 2.549732539343734e+00) * q + 4.374664141464968e+00) * q + 2.938163982698783e+00)
                               / ((((7.784695709041462e-03 * q + 3.224671290700398e-01) * q + 2.445134137142996e+00) * q + 3.754408661907416e+00) * q + 1));
            }

            if (p > high)
            {
                q = Math.Sqrt(-2 * Math.Log(1 - p));
                return -(float)((((((-7.784894002430293e-03 * q - 3.223964580411365e-01) * q - 2.400758277161838e+00) * q - 2.549732539343734e+00) * q + 4.374664141464968e+00) * q + 2.938163982698783e+00)
                                / ((((7.784695709041462e-03 * q + 3.224671290700398e-01) * q + 2.445134137142996e+00) * q + 3.754408661907416e+00) * q + 1));
            }

            q = p - 0.5;
            r = q * q;
            return (float)((((((-3.969683028665376e+01 * r + 2.209460984245205e+02) * r - 2.759285104469687e+02) * r + 1.383577518672690e+02) * r - 3.066479806614716e+01) * r + 2.506628277459239e+00) * q
                           / (((((-5.447609879822406e+01 * r + 1.615858368580409e+02) * r - 1.556989798598866e+02) * r + 6.680131188771972e+01) * r - 1.328068155288572e+01) * r + 1));
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        private static float Clamp(float v, float lo, float hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>Two floats, returned by value (no UnityEngine.Vector2 in this pure file).</summary>
        private struct Vector2Like
        {
            public float X, Y;
        }
    }
}
