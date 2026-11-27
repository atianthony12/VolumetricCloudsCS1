# Offline checks of Sky/CloudLifecycle.cs (cloud lifecycles: clouds form, hold and dissolve while they drift).
# A pure class: the SOURCE is compiled directly, no game and no built DLL needed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-lifecycle.ps1
#
# What it guards: row 0 is the plain map (the look is the read itself, the blend returns it bit for bit); the two
# layer weights are a triangle pair that sums to 1; a layer's offset changes only where that layer's weight is 0; the
# blended value never jumps (the largest change per 1/60 s of game time, at the layers' jumps too, stays tiny); the
# phase, the offsets and the look are periodic in the map's repeat, and the cycle index wraps at CycleRepeat without a
# step; the map's seed shifts the phase field; the score table maps back the map's own values; the cover table gives the
# mean amount it is solved for.

$ErrorActionPreference = "Stop"

$sky = Join-Path $PSScriptRoot "..\VolumetricClouds\Sky"
$src = Join-Path $sky "CloudLifecycle.cs"

$tests = @'
using System;
using System.Collections.Generic;
using VolumetricClouds.Sky;

public static class LifeTests
{
    static List<string> lines = new List<string>();

    static void Check(string name, bool ok, string detail)
    {
        lines.Add((ok ? "ok    " : "FAIL  ") + name + "  " + detail);
    }

    // A synthetic periodic map: value noise over 64 lattice cells per repeat, random values from an integer hash.
    static float Hash01(int x, int y, int salt)
    {
        int h = (x * 73856093) ^ (y * 19349663) ^ (salt * 83492791);
        h = (h ^ (h >> 13)) * 1274126177;
        h = h ^ (h >> 16);
        return (h & 0xffff) / 65535f;
    }

    static float Map(float u, float v)
    {
        float x = u * 64f, y = v * 64f;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        float tx = x - ix, ty = y - iy;
        float sx = tx * tx * (3f - 2f * tx), sy = ty * ty * (3f - 2f * ty);
        int mx0 = ((ix % 64) + 64) % 64, mx1 = (mx0 + 1) % 64, my0 = ((iy % 64) + 64) % 64, my1 = (my0 + 1) % 64;
        float a = Hash01(mx0, my0, 1) + (Hash01(mx1, my0, 1) - Hash01(mx0, my0, 1)) * sx;
        float b = Hash01(mx0, my1, 1) + (Hash01(mx1, my1, 1) - Hash01(mx0, my1, 1)) * sx;
        return a + (b - a) * sy;
    }

    const int Cells = 12, SaltX = 17, SaltY = 5;
    const float Snap = 1f / 96f;

    static CloudLifecycle.Look At(float u, float v, double t)
    {
        return CloudLifecycle.LookAt(u, v, CloudLifecycle.ClockFraction(t), CloudLifecycle.ClockCycle(t), Cells, SaltX, SaltY, Snap, true);
    }

    static float Value(float u, float v, double t)
    {
        CloudLifecycle.Look l = At(u, v, t);
        return CloudLifecycle.Blend(Map(u + l.U0, v + l.V0), Map(u + l.U1, v + l.V1), 0.5f, l);
    }

    public static string[] Run()
    {
        Random rnd = new Random(7);

        // ---- the clock -------------------------------------------------------------------------------------
        Check("clock: 100% = one cycle per 480 s; row 0 = off; 50% = half",
            Math.Abs(CloudLifecycle.CyclesPerSecond(1f) - 1.0 / 480.0) < 1e-12 && CloudLifecycle.CyclesPerSecond(0f) == 0.0 &&
            Math.Abs(CloudLifecycle.CyclesPerSecond(0.5f) - 0.5 / 480.0) < 1e-12 && !CloudLifecycle.Active(0f) && CloudLifecycle.Active(0.01f), "");
        Check("clock: frac and cycle index wrap at CycleRepeat (t = -0.5 -> cycle 255, frac 0.5)",
            CloudLifecycle.ClockCycle(-0.5) == 255 && Math.Abs(CloudLifecycle.ClockFraction(-0.5) - 0.5f) < 1e-6 &&
            CloudLifecycle.ClockCycle(255.5) == 255 && CloudLifecycle.ClockCycle(256.25) == 0 && CloudLifecycle.ClockCycle(1e9) >= 0, "");
        // The empty-sky skip's bound (2026-11-25): no blended value of a cell beats SkipBound at any weight the cell can
        // have (brute force over random cells and a grid of weights), and the bound is tight (the grid reaches it).
        {
            Random skRnd = new Random(7);
            int skBad = 0;
            double skGap = 0.0;
            for (int sk = 0; sk < 2000; sk++)
            {
                float skMean = (float)skRnd.NextDouble();
                float skM0 = (float)skRnd.NextDouble(), skM1 = (float)skRnd.NextDouble();
                float skBound = CloudLifecycle.SkipBound(skMean, skM0, skM1);
                double skBest = double.MinValue;
                for (int sj = 0; sj <= 400; sj++)
                {
                    double sw = sj / 400.0;
                    double sn = Math.Sqrt(sw * sw + (1 - sw) * (1 - sw));
                    double sv = skMean + (sw * (skM0 - skMean) + (1 - sw) * (skM1 - skMean)) / sn;
                    if (sv > skBound + 1e-5) skBad++;
                    if (sv > skBest) skBest = sv;
                }
                skGap = Math.Max(skGap, skBound - skBest);
            }
            Check("empty-sky skip bound: no blended value of a cell beats the bound (2000 cells x 401 weights)", skBad == 0, "(" + skBad + " over)");
            Check("empty-sky skip bound is tight (the weight grid reaches it within 0.001)", skGap < 0.001, "(worst gap " + skGap.ToString("G3") + ")");
        }
        // The C# twin (2026-11-25): TwinValue equals the blend written out (the shader's formula, from the same look's
        // weights and offsets) at sampled points; row 0 is the plain read; the salts of negative seeds stay in 0..30.
        {
            Random twRnd = new Random(11);
            int twBad = 0;
            double twWorst = 0.0;
            for (int tw = 0; tw < 3000; tw++)
            {
                float twU = (float)twRnd.NextDouble(), twV = (float)twRnd.NextDouble();
                double twT = twRnd.NextDouble() * 1000.0;
                int twScale = 1 + twRnd.Next(4);
                int twCells = CloudLifecycle.CellsFor(CloudLifecycle.CellsPer13km, twScale);
                float twSnap = CloudLifecycle.SnapFor(384 * twScale);
                int twSx, twSy;
                CloudLifecycle.SaltsFor(twRnd.Next(-2000000, 2000000), out twSx, out twSy);
                float a0 = (float)twRnd.NextDouble(), a1 = (float)twRnd.NextDouble(), mean = (float)twRnd.NextDouble();
                CloudLifecycle.Look twLook;
                float got = CloudLifecycle.TwinValue(a0, a1, mean, twU, twV, CloudLifecycle.ClockFraction(twT),
                    CloudLifecycle.ClockCycle(twT), twCells, twSx, twSy, twSnap, true, out twLook);
                double n = Math.Sqrt(twLook.W0 * twLook.W0 + twLook.W1 * twLook.W1);
                double want = mean + (twLook.W0 * (a0 - mean) + twLook.W1 * (a1 - mean)) / n;
                double gap = Math.Abs(got - want);
                twWorst = Math.Max(twWorst, gap);
                if (gap > 1e-5 || twLook.W0 < 0f || twLook.W1 < 0f || twLook.U0 < 0f || twLook.U1 < twLook.U0 - 1f) twBad++;
            }
            Check("twin: TwinValue equals the written-out blend at 3000 sampled places (worst gap " + twWorst.ToString("G3") + ")", twBad == 0, "(" + twBad + " off)");

            CloudLifecycle.Look twOff;
            float twRow0 = CloudLifecycle.TwinValue(0.3f, 0.9f, 0.5f, 0.2f, 0.7f, 0.4f, 17, 4, 3, 5, 0.04f, false, out twOff);
            Check("twin: row 0 returns the plain read (drawn0) bit for bit", twRow0 == 0.3f && twOff.Plain, "");

            int sx1, sy1, sx2, sy2;
            CloudLifecycle.SaltsFor(-1, out sx1, out sy1);
            CloudLifecycle.SaltsFor(12345, out sx2, out sy2);
            Check("salts: negative seeds stay in 0..30; seed 12345 gives 12345 % 31 = " + (12345 % 31) + " and " + ((12345 / 31) % 31),
                sx1 >= 0 && sx1 < 31 && sy1 >= 0 && sy1 < 31 && sx2 == 12345 % 31 && sy2 == (12345 / 31) % 31, "(" + sx1 + "," + sy1 + ") (" + sx2 + "," + sy2 + ")");
        }
        // The re-solve (2026-11-25, round 3 (d)): solved on the BLENDED histogram at every grid step, the mean cover holds the
        // coverage asked for at every step (a random map, 64^2, coverages 10/50/90%; cover = the histogram's mean amount).
        {
            Random rsRnd = new Random(5);
            int rsSide = 64;
            float[] rsMap = new float[rsSide * rsSide];
            double rsSum = 0.0;
            for (int i = 0; i < rsMap.Length; i++)
            {
                rsMap[i] = (float)rsRnd.NextDouble();
                rsSum += rsMap[i];
            }
            float rsMean = (float)(rsSum / rsMap.Length);
            int rsCells = CloudLifecycle.CellsFor(CloudLifecycle.CellsPer13km, 1);
            int rsCount = CloudLifecycle.ResolveCount(1f);
            Check("re-solve grid: 100% = 2048 solves per repeat (one per 60 s of sim time at 1x)", rsCount == 2048, "(" + rsCount + ")");
            int[] rsHist = new int[256];
            double rsWorst = 0.0;
            foreach (double rsCov in new double[] { 0.1, 0.5, 0.9 })
            {
                for (int rsK = 0; rsK < rsCount; rsK += 128)
                {
                    double rsClock = CloudLifecycle.ResolveClock(rsK, 1f);
                    long rsTotal = CloudLifecycle.BlendHistogram(rsMap, rsSide, rsMean, rsClock, 1f, rsCells, 3, 9, rsHist);
                    float rsT = CloudLifecycle.CoverTable.Solve(rsHist, rsTotal, (float)rsCov, CloudLifecycle.Softness);
                    double amount = 0.0;
                    for (int b = 0; b < 256; b++)
                        amount += CloudLifecycle.Amount((b + 0.5f) / 256f, rsT) * rsHist[b];
                    double dev = Math.Abs(amount / rsTotal - rsCov);
                    rsWorst = Math.Max(rsWorst, dev);
                }
            }
            Check("re-solve: the blended map's mean cover holds the coverage at every 16th grid step (10/50/90%), worst " + rsWorst.ToString("F4"),
                rsWorst <= 0.02, "");
        }
        // The clock at a whole cycle (2026-11-25, the bug the whole-map gate found): a clock a hair below 2.0 must read the
        // same cycle and fraction as 2.0 and blend to the same histogram. Before the fix the pair was (fraction 0, cycle 1).
        {
            double ctT = 2.0 - 3e-15;
            Check("clock at a whole cycle: a hair below 2.0 reads cycle 2 and fraction 0 (as 2.0 does)",
                CloudLifecycle.ClockCycle(ctT) == 2 && CloudLifecycle.ClockFraction(ctT) == 0f && CloudLifecycle.ClockCycle(2.0) == 2,
                "(cycle " + CloudLifecycle.ClockCycle(ctT) + ", fraction " + CloudLifecycle.ClockFraction(ctT) + ")");
            Random ctRnd = new Random(9);
            int ctSide = 64;
            float[] ctMap = new float[ctSide * ctSide];
            for (int i = 0; i < ctMap.Length; i++)
                ctMap[i] = (float)ctRnd.NextDouble();
            int[] ctA = new int[256], ctB = new int[256];
            long ctTa = CloudLifecycle.BlendHistogram(ctMap, ctSide, 0.5f, ctT, 1f, 4, 7, 13, ctA);
            long ctTb = CloudLifecycle.BlendHistogram(ctMap, ctSide, 0.5f, 2.0, 1f, 4, 7, 13, ctB);
            double ctL1 = 0.0;
            for (int b = 0; b < 256; b++)
                ctL1 += Math.Abs((double)ctA[b] / ctTa - (double)ctB[b] / ctTb);
            Check("clock at a whole cycle: the blended histogram just below 2.0 equals the one at 2.0 (L1 " + ctL1.ToString("G3") + ")", ctL1 < 0.002, "");
        }
        // The cadence share (2026-11-25): row 0 never asks for a read; a clock move of half the limit is half the share.
        Check("cadence share: 0 at row 0, and linear in the clock's move (half the limit = half a share)",
            CloudLifecycle.ChangeShare(0.0, 0.005, 0f) == 0.0 && Math.Abs(CloudLifecycle.ChangeShare(0.0, 0.005, 1f) - 0.5) < 1e-12 &&
            Math.Abs(CloudLifecycle.ChangeShare(0.005, 0.0, 1f) - 0.5) < 1e-12, "");
        // 2026-11-25 (advisor): the wrap at CycleRepeat is a small step, not 255 cycles; a first reading (NaN) is no share.
        Check("cadence share: a step across the cycle wrap is the small step (256 -> 0 and back), NaN is 0",
            Math.Abs(CloudLifecycle.ClockDelta(255.9, 0.1) - 0.2) < 1e-9 && Math.Abs(CloudLifecycle.ClockDelta(0.1, 255.9) + 0.2) < 1e-9 &&
            Math.Abs(CloudLifecycle.ChangeShare(255.99, 0.01, 1f) - 2.0) < 1e-6 && CloudLifecycle.ChangeShare(double.NaN, 1.0, 1f) == 0.0,
            "wrap " + CloudLifecycle.ClockDelta(255.9, 0.1).ToString("F4"));
        // 2026-11-25 (column map's own limit): 0.06 cycles is one whole share; the shadow map's 3-argument form keeps 0.01.
        Check("cadence share: the column map's limit (0.06 cycles) is one share; the shadow map's stays 0.01",
            Math.Abs(CloudLifecycle.ChangeShare(0.0, 0.06, 1f, CloudLifecycle.ColumnCycleLimit) - 1.0) < 1e-9 &&
            Math.Abs(CloudLifecycle.ChangeShare(0.0, 0.01, 1f) - 1.0) < 1e-9 && CloudLifecycle.ChangeShare(0.0, 0.06, 0f, 0.06) == 0.0, "");
        // A clock jump (Restore) is counted, so the cadences read it as an input change.
        long jumpsBefore = CloudLifecycle.Jumps;
        CloudLifecycle.Restore(12.5);
        Check("a restore counts as a clock jump (Jumps + 1) and sets the clock", CloudLifecycle.Jumps == jumpsBefore + 1 && Math.Abs(CloudLifecycle.Clock - 12.5) < 1e-12, "");
        // The heaps' line in the blended scores (2026-11-25, task 1): ZAtShare gives the score whose share above it is the
        // target; ShareAbove returns it back; row 0's blended score histogram is the plain score histogram bit for bit.
        int[] shist = new int[CloudLifecycle.ScoreBins];
        long stot = 0;
        for (int b = 0; b < CloudLifecycle.ScoreBins; b++)
        {
            shist[b] = 1 + (b % 7);
            stot += shist[b];
        }
        float zt = CloudLifecycle.ZAtShare(shist, stot, 0.25f);
        float shareBack = CloudLifecycle.ShareAbove(shist, stot, zt);
        Check("ZAtShare inverts ShareAbove (share 0.25 -> z -> share " + shareBack.ToString("F4") + ")", Math.Abs(shareBack - 0.25f) < 0.01f, "");
        byte[] plainBytes = new byte[96 * 96];
        Random rng = new Random(7);
        for (int i = 0; i < plainBytes.Length; i++)
            plainBytes[i] = (byte)rng.Next(256);
        int[] plainH = new int[CloudLifecycle.ScoreBins], rowZeroH = new int[CloudLifecycle.ScoreBins];
        for (int i = 0; i < plainBytes.Length; i++)
            plainH[CloudLifecycle.ScoreBin(CloudLifecycle.ScoreOfByte(plainBytes[i]))]++;
        CloudLifecycle.BlendScoreHistogram(plainBytes, 96, 123.4, 0f, 4, 3, 5, rowZeroH);
        bool same = true;
        for (int b = 0; b < plainH.Length; b++)
            same &= plainH[b] == rowZeroH[b];
        Check("row 0: the blended score histogram IS the plain one (no offset, no blend)", same, "");
        // The advancing clock (2026-11-25): one cycle per 480 s of sim time at 100%; 0% and a pause never move it; the
        // clock stays in [0, CycleRepeat), so the wrap is exact for the cycle index (a whole number of repeats).
        CloudLifecycle.Restore(0.0);
        CloudLifecycle.Advance(1f, 480.0);
        Check("clock advances: 100% = one cycle per 480 s of sim time", Math.Abs(CloudLifecycle.Clock - 1.0) < 1e-9, "(clock " + CloudLifecycle.Clock.ToString("R") + ")");
        CloudLifecycle.Advance(0f, 480.0);
        Check("clock holds at 0%", Math.Abs(CloudLifecycle.Clock - 1.0) < 1e-9, "");
        CloudLifecycle.Advance(1f, 0.0);
        Check("clock holds while paused (0 s)", Math.Abs(CloudLifecycle.Clock - 1.0) < 1e-9, "");
        CloudLifecycle.Restore(-0.5);
        Check("clock restores wrapped into [0, CycleRepeat)", Math.Abs(CloudLifecycle.Clock - 255.5) < 1e-9, "(clock " + CloudLifecycle.Clock.ToString("R") + ")");
        CloudLifecycle.Advance(1f, 240.0);
        Check("clock wraps at CycleRepeat without a jump in the cycle index", CloudLifecycle.Clock < 1e-9 && CloudLifecycle.ClockCycle(CloudLifecycle.Clock) == 0, "(clock " + CloudLifecycle.Clock.ToString("R") + ")");
        CloudLifecycle.Restore(0.0);

        // ---- row 0 = the plain map ---------------------------------------------------------------------------
        bool plainOk = true;
        for (int i = 0; i < 2000; i++)
        {
            float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble();
            CloudLifecycle.Look l = CloudLifecycle.LookAt(u, v, 0.3f, 17, Cells, SaltX, SaltY, Snap, false);
            float a0 = Map(u, v), a1 = Map(u + 0.3f, v + 0.1f);
            if (!l.Plain || l.W0 != 1f || l.W1 != 0f || l.U0 != 0f || l.V0 != 0f || l.U1 != 0f || l.V1 != 0f) plainOk = false;
            if (CloudLifecycle.Blend(a0, a1, 0.5f, l) != a0) plainOk = false;
            if (CloudLifecycle.BlendScore(0.25f, -1.5f, l) != 0.25f) plainOk = false;
        }
        Check("row 0: the look is the plain read (weight 1, offset 0) and the blend returns the read bit for bit", plainOk, "");

        // ---- the weights ------------------------------------------------------------------------------------------
        float worstSum = 0f; bool inRange = true;
        for (int i = 0; i < 20000; i++)
        {
            float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble();
            double t = rnd.NextDouble() * 300.0;
            CloudLifecycle.Look l = At(u, v, t);
            worstSum = Math.Max(worstSum, Math.Abs(l.W0 + l.W1 - 1f));
            if (l.W0 < -1e-6f || l.W1 < -1e-6f || l.W0 > 1f + 1e-6f || l.W1 > 1f + 1e-6f) inRange = false;
        }
        Check("weights: w0 + w1 = 1, each in 0..1", worstSum < 1e-5f && inRange, "(worst |sum - 1| " + worstSum.ToString("G3") + ")");

        // ---- the offsets change only at weight 0 ----------------------------------------------------------------
        int jumps0 = 0, jumps1 = 0; float worstW = 0f; bool jumpsOnlyAtZero = true;
        for (int p = 0; p < 300; p++)
        {
            float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble();
            double baseT = rnd.Next(0, 200);
            CloudLifecycle.Look prev = At(u, v, baseT);
            for (int s = 1; s <= 4000; s++)
            {
                double t = baseT + s / 4000.0 * 1.5;
                CloudLifecycle.Look cur = At(u, v, t);
                if (cur.U0 != prev.U0 || cur.V0 != prev.V0)
                {
                    jumps0++;
                    float w = Math.Max(Math.Max(prev.W0, cur.W0), 0f);
                    worstW = Math.Max(worstW, w);
                    if (w > 0.002f) jumpsOnlyAtZero = false;
                }
                if (cur.U1 != prev.U1 || cur.V1 != prev.V1)
                {
                    jumps1++;
                    float w = Math.Max(Math.Max(prev.W1, cur.W1), 0f);
                    worstW = Math.Max(worstW, w);
                    if (w > 0.002f) jumpsOnlyAtZero = false;
                }
                prev = cur;
            }
        }
        Check("offsets: a layer's offset changes only while its weight is 0 (fine sweep, 300 places, 1.5 cycles each)",
            jumpsOnlyAtZero && jumps0 > 100 && jumps1 > 100,
            "(layer 0 jumped " + jumps0 + " times, layer 1 " + jumps1 + " times; largest weight at a jump " + worstW.ToString("G3") + ")");

        // ---- the seam: the change per 1/60 s of game time, at any time, jumps included --------------------------
        float dt = (float)(1.0 / 60.0 / CloudLifecycle.SecondsPerCycleAt100);
        float worstStep = 0f;
        for (int i = 0; i < 40000; i++)
        {
            float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble();
            double t = rnd.NextDouble() * 512.0;
            float a = Value(u, v, t), b = Value(u, v, t + dt);
            worstStep = Math.Max(worstStep, Math.Abs(a - b));
        }
        // Around each jump: find the jump times of a few places and compare the value either side of them.
        float worstAtJump = 0f;
        for (int p = 0; p < 300; p++)
        {
            float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble();
            double baseT = rnd.Next(0, 200);
            CloudLifecycle.Look prev = At(u, v, baseT);
            for (int s = 1; s <= 4000; s++)
            {
                double t = baseT + s / 4000.0 * 1.5;
                CloudLifecycle.Look cur = At(u, v, t);
                if (cur.U0 != prev.U0 || cur.V0 != prev.V0 || cur.U1 != prev.U1 || cur.V1 != prev.V1)
                {
                    float before = Value(u, v, t - 1e-9), after = Value(u, v, t + 1e-9);
                    worstAtJump = Math.Max(worstAtJump, Math.Abs(after - before));
                }
                prev = cur;
            }
        }
        Check("seam: the value changes by almost nothing per 1/60 s, at the jumps too",
            worstStep < 0.002f && worstAtJump < 0.002f,
            "(largest change per 1/60 s " + worstStep.ToString("G3") + ", across a jump " + worstAtJump.ToString("G3") + ")");

        // ---- tiling -----------------------------------------------------------------------------------------------
        bool tileOk = true; float worstTile = 0f;
        for (int i = 0; i < 4000; i++)
        {
            float u = (float)(rnd.Next(0, 4096)) / 4096f, v = (float)(rnd.Next(0, 4096)) / 4096f;   // dyadic: exact shifts by 1
            double t = rnd.NextDouble() * 400.0;
            float p0 = CloudLifecycle.Phase(u, v, Cells, SaltX, SaltY), p1 = CloudLifecycle.Phase(u + 1f, v - 2f, Cells, SaltX, SaltY);
            worstTile = Math.Max(worstTile, Math.Abs(p0 - p1));
            CloudLifecycle.Look l0 = At(u, v, t), l1 = At(u + 1f, v - 2f, t);
            if (Math.Abs(l0.U0 - l1.U0) > 1e-6f || Math.Abs(l0.V1 - l1.V1) > 1e-6f || Math.Abs(l0.W0 - l1.W0) > 1e-5f) tileOk = false;
        }
        Check("tiling: the phase and the look are identical one repeat apart (and across the drift wrap)", tileOk && worstTile < 1e-5f, "(largest phase difference " + worstTile.ToString("G3") + ")");

        bool wrapOk = true;
        for (int i = 0; i < 4000; i++)
        {
            float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble();
            float f = (float)rnd.NextDouble();
            int c = rnd.Next(0, CloudLifecycle.CycleRepeat);
            CloudLifecycle.Look a = CloudLifecycle.LookAt(u, v, f, c, Cells, SaltX, SaltY, Snap, true);
            CloudLifecycle.Look b = CloudLifecycle.LookAt(u, v, f, c + CloudLifecycle.CycleRepeat, Cells, SaltX, SaltY, Snap, true);
            if (a.U0 != b.U0 || a.V0 != b.V0 || a.U1 != b.U1 || a.V1 != b.V1) wrapOk = false;
        }
        Check("cycle index: the offsets are the same at cycle c and c + CycleRepeat", wrapOk, "");

        bool crossOk = true; int crossJumps = 0;
        for (int p = 0; p < 200; p++)
        {
            float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble();
            double baseT = CloudLifecycle.CycleRepeat - 3.0 + rnd.NextDouble();
            CloudLifecycle.Look prev = At(u, v, baseT);
            for (int s = 1; s <= 9000; s++)
            {
                double t = baseT + s / 9000.0 * 6.0;
                CloudLifecycle.Look cur = At(u, v, t);
                if (cur.U0 != prev.U0 || cur.V0 != prev.V0 || cur.U1 != prev.U1 || cur.V1 != prev.V1)
                {
                    crossJumps++;
                    if (Math.Max(prev.W0, cur.W0) > 0.002f && cur.U0 != prev.U0) crossOk = false;
                    if (Math.Max(prev.W1, cur.W1) > 0.002f && cur.U1 != prev.U1) crossOk = false;
                }
                prev = cur;
            }
        }
        Check("cycle index across the wrap (t 253..259): offsets still jump only at weight 0", crossOk && crossJumps > 50, "(" + crossJumps + " jumps across the wrap)");

        // ---- seed ------------------------------------------------------------------------------------------------------
        int differ = 0;
        for (int i = 0; i < 400; i++)
        {
            float u = (float)rnd.NextDouble(), v = (float)rnd.NextDouble();
            if (CloudLifecycle.Phase(u, v, Cells, SaltX, SaltY) != CloudLifecycle.Phase(u, v, Cells, SaltX + 3, SaltY + 7)) differ++;
        }
        Check("seed: another map seed gives another phase field, the same seed the same one",
            differ > 380 && CloudLifecycle.Phase(0.3f, 0.6f, Cells, SaltX, SaltY) == CloudLifecycle.Phase(0.3f, 0.6f, Cells, SaltX, SaltY),
            "(" + differ + " of 400 places differ)");

        // ---- the spread of the phase: at least two cycles across the sky -------------------------------------------------
        float pmin = float.MaxValue, pmax = float.MinValue;
        for (int i = 0; i < 96; i++)
            for (int j = 0; j < 96; j++)
            {
                float p = CloudLifecycle.Phase((i + 0.5f) / 96f, (j + 0.5f) / 96f, Cells, SaltX, SaltY);
                pmin = Math.Min(pmin, p); pmax = Math.Max(pmax, p);
            }
        Check("phase spans the full Span (2 cycles) across a repeat", pmax - pmin >= 0.95f * CloudLifecycle.Span && pmin >= 0f && pmax <= CloudLifecycle.Span + 1e-4f,
            "(from " + pmin.ToString("F2") + " to " + pmax.ToString("F2") + " cycles)");

        // ---- the equaliser: the phase is near-uniform over a repeat (many cells: the table was measured on the raw noise) ----
        int[] decile = new int[10]; int nDec = 0;
        for (int i = 0; i < 300; i++)
            for (int j = 0; j < 300; j++)
            {
                float p = CloudLifecycle.Phase((i + 0.5f) / 300f, (j + 0.5f) / 300f, 24, SaltX, SaltY);
                float f = p - (float)Math.Floor(p / CloudLifecycle.Span) * CloudLifecycle.Span;
                decile[Math.Min(9, (int)(f / CloudLifecycle.Span * 10f))]++; nDec++;
            }
        float worstDec = 0f;
        foreach (int c in decile) worstDec = Math.Max(worstDec, Math.Abs(c / (float)nDec - 0.1f));
        Check("equaliser: the phase is spread near-evenly over a repeat (24 cells; deciles within 3 points)", worstDec < 0.03f,
            "(largest decile off by " + (100f * worstDec).ToString("F1") + " points)");

        // ---- the score table maps back the map's own values --------------------------------------------------------------
        float[] values = new float[20000];
        for (int i = 0; i < values.Length; i++)
            values[i] = Map((float)rnd.NextDouble(), (float)rnd.NextDouble());
        float[] z = CloudLifecycle.ScoresOf(values);
        float[] table = CloudLifecycle.ValueTable(values);
        float worstBack = 0f; bool zMono = true;
        float[] sortedValues = (float[])values.Clone(); Array.Sort(sortedValues);
        for (int i = 0; i < values.Length; i++)
        {
            float back = CloudLifecycle.ValueAt(table, z[i]);
            worstBack = Math.Max(worstBack, Math.Abs(back - values[i]));
        }
        for (int i = 1; i < 2000; i++)
        {
            if (CloudLifecycle.ValueAt(table, -3f + i * 0.003f) < CloudLifecycle.ValueAt(table, -3f + (i - 1) * 0.003f) - 1e-6f) zMono = false;
        }
        Check("score table: the map's values come back through the scores (within the table's step), monotone", zMono && worstBack < 0.06f,
            "(largest error " + worstBack.ToString("F4") + " on a value range of 0..1)");

        // ---- the cover table gives the mean amount it is solved for ------------------------------------------------------
        float[] uniform = new float[200000];
        for (int i = 0; i < uniform.Length; i++) uniform[i] = (float)rnd.NextDouble();
        CloudLifecycle.CoverTable cover = CloudLifecycle.CoverTable.FromValues(uniform);
        float worstCover = 0f;
        foreach (float c in new float[] { 0.1f, 0.3f, 0.5f, 0.6f, 0.9f })
        {
            float thr = cover.GetThreshold(c); double sum = 0.0;
            foreach (float x in uniform) sum += CloudLifecycle.Amount(x, thr);
            worstCover = Math.Max(worstCover, Math.Abs((float)(sum / uniform.Length) - c));
        }
        Check("cover table: the mean amount at the threshold is the coverage asked for (uniform map)", worstCover < 0.01f, "(worst " + worstCover.ToString("F4") + ")");

        // ---- the clouds' table under the lifecycle keeps the PLAIN sky's real cover (2026-11-25 "overriding the intensity") ----
        // The plain threshold is solved on the stored values, the reads are decoded (here x^2.2): the plain sky covers less
        // than asked. The blended table must cover what the plain sky REALLY covers, on the blended values.
        int bins = CloudLifecycle.CoverTable.Bins;
        int[] storedHist = new int[bins], drawnHist = new int[bins], blendHist = new int[bins];
        float[] drawnVals = new float[uniform.Length], blendVals = new float[uniform.Length];
        for (int i = 0; i < uniform.Length; i++)
        {
            drawnVals[i] = (float)Math.Pow(uniform[i], 2.2);
            blendVals[i] = 0.5f * drawnVals[i] + 0.5f * (float)Math.Pow(uniform[(i * 7919) % uniform.Length], 2.2);
            storedHist[Math.Min(bins - 1, (int)(uniform[i] * bins))]++;
            drawnHist[Math.Min(bins - 1, (int)(drawnVals[i] * bins))]++;
            blendHist[Math.Min(bins - 1, (int)(blendVals[i] * bins))]++;
        }
        float[] plainTable = new float[33];
        for (int s = 0; s <= 32; s++) plainTable[s] = CloudLifecycle.CoverTable.Solve(storedHist, uniform.Length, s / 32f, CloudLifecycle.Softness);
        float worstSame = 0f, worstBlend = 0f, worstAsked = 0f;
        CloudLifecycle.CoverTable sameT = CloudLifecycle.CoverTable.MatchingPlain(drawnHist, uniform.Length, drawnHist, uniform.Length, plainTable);
        CloudLifecycle.CoverTable blendT = CloudLifecycle.CoverTable.MatchingPlain(blendHist, uniform.Length, drawnHist, uniform.Length, plainTable);
        foreach (float c in new float[] { 0.2f, 0.4f, 0.6f, 0.8f })
        {
            double plainSum = 0.0, sameSum = 0.0, blendSum = 0.0;
            float tPlain = CloudLifecycle.CoverTable.Lerped(plainTable, c), tSame = sameT.GetThreshold(c), tBlend = blendT.GetThreshold(c);
            for (int i = 0; i < uniform.Length; i++)
            {
                plainSum += CloudLifecycle.Amount(drawnVals[i], tPlain);
                sameSum += CloudLifecycle.Amount(drawnVals[i], tSame);
                blendSum += CloudLifecycle.Amount(blendVals[i], tBlend);
            }
            float plainCover = (float)(plainSum / uniform.Length);
            worstSame = Math.Max(worstSame, Math.Abs((float)(sameSum / uniform.Length) - plainCover));
            worstBlend = Math.Max(worstBlend, Math.Abs((float)(blendSum / uniform.Length) - plainCover));
            worstAsked = Math.Max(worstAsked, c - plainCover);
        }
        Check("lifecycle clouds' table: the plain sky really covers less than asked here (the decoding the clouds are tuned to)", worstAsked > 0.05f, "(largest shortfall " + worstAsked.ToString("F3") + ")");
        Check("lifecycle clouds' table: on the plain map's own values it gives the plain sky's real cover", worstSame < 0.01f, "(worst " + worstSame.ToString("F4") + ")");
        Check("lifecycle clouds' table: on a blended map it gives the plain sky's real cover, not the slider's", worstBlend < 0.01f, "(worst " + worstBlend.ToString("F4") + ")");

        // ---- determinism -------------------------------------------------------------------------------------------------
        Check("determinism: the same inputs give the same look", At(0.123f, 0.456f, 17.3).U1 == At(0.123f, 0.456f, 17.3).U1 && At(0.123f, 0.456f, 17.3).W0 == At(0.123f, 0.456f, 17.3).W0, "");

        return lines.ToArray();
    }
}
'@

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("test-lifecycle-" + [Guid]::NewGuid().ToString("N") + ".cs")
Set-Content -Path $tmp -Value $tests -Encoding UTF8
try {
    Add-Type -Path @($src, $tmp)
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

$failed = 0
foreach ($line in [LifeTests]::Run()) {
    Write-Host $line
    if ($line.StartsWith("FAIL")) { $failed++ }
}

Write-Host ""
if ($failed -gt 0) { Write-Host "$failed check(s) FAILED"; exit 1 }
Write-Host "all checks passed"
