# Twin test of the cloud lifecycle's SHADER code (UnityProject/Assets/VolumetricClouds/CloudCommon.cginc, under VC_PLUS:
# LifeEq, LifeEqualise, LifeHash, LifeNoise, LifeLookAt, LifeBlend) against Sky/CloudLifecycle.cs (TwinValue, LookAt, Blend,
# BlendScore). The HLSL is written out line by line in C# (float32 throughout) with the uniforms as CloudShaderParams packs
# them (_LifeClock, _LifeGrid, _LifeWindow, _LifeEqual[9]). The GPU itself is not run here (the bench does that).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-lifeshader.ps1
#
# Places sampled: random; just before a layer's phase wraps (its weight near 0, the offset about to change); clock fractions
# that round to 1 in float (t a hair below a whole cycle); both map sizes (384 and 1536 texels) and both salts of two seeds.

$ErrorActionPreference = "Stop"

$sky = Join-Path $PSScriptRoot "..\VolumetricClouds\Sky"
$src = Join-Path $sky "CloudLifecycle.cs"

$tests = @'
using System;
using System.Collections.Generic;
using VolumetricClouds.Sky;

// The shader's lifecycle functions, line by line (float32 throughout). Uniforms are set by SetUniforms, as CloudShaderParams.
public static class LifeShaderPort
{
    static float[] _clock = new float[4];   // _LifeClock: x frac(t), y cycle, z on, w snap
    static float[] _grid = new float[4];    // _LifeGrid: x cells, y saltX, z saltY, w Span
    static float[] _window = new float[4];  // _LifeWindow: x window, y gap
    static float[][] _eq = new float[9][];  // _LifeEqual[9]

    public static void SetUniforms(float clockFrac, float cycle, float on, float snap, float cells, float saltX, float saltY,
                                   float span, float window, float gap, float[] table)
    {
        _clock[0] = clockFrac; _clock[1] = cycle; _clock[2] = on; _clock[3] = snap;
        _grid[0] = cells; _grid[1] = saltX; _grid[2] = saltY; _grid[3] = span;
        _window[0] = window; _window[1] = gap;
        for (int i = 0; i < 9; i++)
        {
            _eq[i] = new float[4];
            for (int j = 0; j < 4; j++)
            {
                int k = i * 4 + j;
                _eq[i][j] = k < table.Length ? table[k] : 0f;
            }
        }
    }

    static float Frac(float v) { return v - (float)Math.Floor(v); }
    static float Saturate(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

    // CloudCommon.cginc CellHash(float2 id, float repeat): the xy of its float3.
    static void CellHash(float idX, float idY, float repeat, out float r0, out float r1)
    {
        idX -= repeat * (float)Math.Floor(idX / repeat);
        idY -= repeat * (float)Math.Floor(idY / repeat);
        float p0 = Frac(idX * 0.1031f + 0.37f);
        float p1 = Frac(idY * 0.1030f + 0.37f);
        float p2 = Frac(idX * 0.0973f + 0.37f);
        float d = p0 * (p1 + 33.33f) + p1 * (p0 + 33.33f) + p2 * (p2 + 33.33f);
        p0 += d; p1 += d; p2 += d;
        r0 = Frac((p0 + p1) * p2);
        r1 = Frac((p0 + p2) * p1);
    }

    // LifeEq(int i): q = _LifeEqual[i >> 2], component i & 3 (the shader's dot with a one-hot vector).
    static float LifeEq(int i)
    {
        return _eq[i >> 2][i & 3];
    }

    // LifeEqualise(float value).
    static float LifeEqualise(float value)
    {
        float t = Saturate(value) * 32f;
        float i = Math.Min((float)Math.Floor(t), 31f);
        float f = t - i;
        int k = (int)i;
        return LifeEq(k) + (LifeEq(k + 1) - LifeEq(k)) * f;
    }

    // LifeHash(float x, float y, float cells).
    static float LifeHash(float x, float y, float cells)
    {
        float xm = x - cells * (float)Math.Floor(x / cells);
        float ym = y - cells * (float)Math.Floor(y / cells);
        float r0, r1;
        CellHash(xm + 64f * _grid[1], ym + 64f * _grid[2], 4096f, out r0, out r1);
        return r0;
    }

    // LifeNoise(float2 uv).
    static float LifeNoise(float u, float v)
    {
        float cells = _grid[0];
        float xu = u * cells;
        float yv = v * cells;
        float f0x = (float)Math.Floor(xu);
        float f0y = (float)Math.Floor(yv);
        float tx = xu - f0x;
        float ty = yv - f0y;
        float sx = tx * tx * (3f - 2f * tx);
        float sy = ty * ty * (3f - 2f * ty);
        float h00 = LifeHash(f0x, f0y, cells);
        float h10 = LifeHash(f0x + 1f, f0y, cells);
        float h01 = LifeHash(f0x, f0y + 1f, cells);
        float h11 = LifeHash(f0x + 1f, f0y + 1f, cells);
        float a = h00 + (h10 - h00) * sx;
        float b = h01 + (h11 - h01) * sx;
        return a + (b - a) * sy;
    }

    // LifeLookAt(float2 uv): returns w = (w0, w1, norm, 0) and off = (U0, V0, U1, V1). With the lifecycle off, the plain look.
    public static void LifeLookAt(float u, float v, float[] w, float[] off)
    {
        if (_clock[2] <= 0.5f)
        {
            w[0] = 1f; w[1] = 0f; w[2] = 1f; w[3] = 0f;
            off[0] = off[1] = off[2] = off[3] = 0f;
            return;
        }

        float p = _grid[3] * LifeEqualise(LifeNoise(u, v));
        float s0 = _clock[0] + p;
        float s1 = s0 + 0.5f;
        float phi0 = s0 - (float)Math.Floor(s0);
        float phi1 = s1 - (float)Math.Floor(s1);
        float w0 = 1f - Math.Abs(2f * phi0 - 1f);
        float w1 = 1f - Math.Abs(2f * phi1 - 1f);
        float n0 = _clock[1] + (float)Math.Floor(s0);
        float n1 = _clock[1] + (float)Math.Floor(s1);
        float h0x, h0y, h1x, h1y;
        CellHash(n0, 0f, 256f, out h0x, out h0y);
        CellHash(n1, 1f, 256f, out h1x, out h1y);
        float snap = _clock[3];
        off[0] = (float)Math.Floor((h0x * _window[0]) / snap) * snap;
        off[1] = (float)Math.Floor((h0y * _window[0]) / snap) * snap;
        off[2] = (float)Math.Floor((_window[1] + h1x * _window[0]) / snap) * snap;
        off[3] = (float)Math.Floor((_window[1] + h1y * _window[0]) / snap) * snap;
        w[0] = w0; w[1] = w1; w[2] = (float)Math.Sqrt(w0 * w0 + w1 * w1); w[3] = 0f;
    }

    // LifeBlend(a0, a1, mean, w): mean + (w.x (a0 - mean) + w.y (a1 - mean)) / w.z (also the score's, mean 0).
    public static float LifeBlend(float a0, float a1, float mean, float[] w)
    {
        return mean + (w[0] * (a0 - mean) + w[1] * (a1 - mean)) / w[2];
    }

    // The port's phase of a place (Span x the equalised noise), for the comparison with CloudLifecycle.Phase.
    public static float PhaseOf(float u, float v)
    {
        return _grid[3] * LifeEqualise(LifeNoise(u, v));
    }

    // The port's equaliser over one raw value (the nine float4s), for the comparison with CloudLifecycle.Equalise.
    public static float EqualiseOf(float raw)
    {
        return LifeEqualise(raw);
    }
}

public static class LifeShaderTests
{
    static List<string> lines = new List<string>();
    static int failures = 0;

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) failures++;
        lines.Add((ok ? "ok    " : "FAIL  ") + name + "  " + detail);
    }

    // The uniforms CloudShaderParams sends for one map size, seed and scale.
    static void Upload(int side, int seed, int scale, int cellsPer13km, float clockFrac, int cycle, bool on)
    {
        int saltX, saltY;
        CloudLifecycle.SaltsFor(seed, out saltX, out saltY);
        int cells = CloudLifecycle.CellsFor(cellsPer13km, scale);
        LifeShaderPort.SetUniforms(clockFrac, cycle, on ? 1f : 0f, CloudLifecycle.SnapFor(side), cells, saltX, saltY,
            CloudLifecycle.Span, CloudLifecycle.LayerWindow, CloudLifecycle.LayerGap, CloudLifecycle.EqualiserTable);
    }

    public static string[] Run()
    {
        Random rng = new Random(20261125);
        double worstW = 0, worstOff = 0, worstBlend = 0, worstScore = 0, worstPhase = 0;
        int offsetsDiffer = 0, samples = 0, nearWrap = 0, nearOne = 0;

        int[] seeds = { 12345, 777 };
        int[] sides = { 384, 1536 };
        int[] scales = { 1, 4 };

        for (int si = 0; si < seeds.Length; si++)
        for (int zi = 0; zi < sides.Length; zi++)
        {
            int seed = seeds[si], side = sides[zi], scale = scales[zi];
            int cellsPer = CloudLifecycle.CellsPer13km;
            int saltX, saltY;
            CloudLifecycle.SaltsFor(seed, out saltX, out saltY);
            int cells = CloudLifecycle.CellsFor(cellsPer, scale);
            float snap = CloudLifecycle.SnapFor(side);

            for (int n = 0; n < 1600; n++)
            {
                float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
                double t;
                int kind = n % 3;
                if (kind == 0)
                {
                    t = rng.NextDouble() * CloudLifecycle.CycleRepeat;
                }
                else if (kind == 1)
                {
                    // Just before this place's layer 0 phase wraps (its weight near 0): t = k + (whole - P) - 1e-5.
                    float p = CloudLifecycle.Phase(u, v, cells, saltX, saltY);
                    double whole = Math.Floor(p) + 1.0;
                    double frac = whole - p - 1e-5;
                    if (frac <= 0.0 || frac >= 1.0)
                        continue;
                    t = Math.Floor(rng.NextDouble() * (CloudLifecycle.CycleRepeat - 1)) + frac;
                    nearWrap++;
                }
                else
                {
                    // A clock a hair below a whole cycle: the float fraction rounds to 1 (ClockFraction gives 0, the cycle the next).
                    t = Math.Floor(rng.NextDouble() * (CloudLifecycle.CycleRepeat - 1)) + 1.0 - 1e-9;
                    nearOne++;
                }

                float clockFrac = CloudLifecycle.ClockFraction(t);
                int cycle = CloudLifecycle.ClockCycle(t);
                Upload(side, seed, scale, cellsPer, clockFrac, cycle, true);

                float[] pw = new float[4];
                float[] poff = new float[4];
                LifeShaderPort.LifeLookAt(u, v, pw, poff);

                CloudLifecycle.Look look = CloudLifecycle.LookAt(u, v, clockFrac, cycle, cells, saltX, saltY, snap, true);
                worstW = Math.Max(worstW, Math.Max(Math.Abs(pw[0] - look.W0), Math.Abs(pw[1] - look.W1)));
                double dOff = Math.Max(Math.Max(Math.Abs(poff[0] - look.U0), Math.Abs(poff[1] - look.V0)),
                                       Math.Max(Math.Abs(poff[2] - look.U1), Math.Abs(poff[3] - look.V1)));
                if (dOff > 1e-7)
                    offsetsDiffer++;
                worstOff = Math.Max(worstOff, dOff);

                // The blend: the twin's TwinValue against the port's formula on the same two reads.
                float a0 = (float)rng.NextDouble(), a1 = (float)rng.NextDouble(), mean = 0.4f + 0.2f * (float)rng.NextDouble();
                float twin = CloudLifecycle.TwinValue(a0, a1, mean, u, v, clockFrac, cycle, cells, saltX, saltY, snap, true, out look);
                float port = LifeShaderPort.LifeBlend(a0, a1, mean, pw);
                worstBlend = Math.Max(worstBlend, Math.Abs(twin - port));

                // The score: BlendScore over the normal scores (mean 0) against the port's blend with mean 0.
                float z0 = (float)(rng.NextDouble() * 6.0 - 3.0), z1 = (float)(rng.NextDouble() * 6.0 - 3.0);
                float scoreTwin = CloudLifecycle.BlendScore(z0, z1, look);
                float scorePort = LifeShaderPort.LifeBlend(z0, z1, 0f, pw);
                worstScore = Math.Max(worstScore, Math.Abs(scoreTwin - scorePort));

                // The phase itself: the port's noise through the equaliser against CloudLifecycle.Phase.
                float phaseTwin = CloudLifecycle.Phase(u, v, cells, saltX, saltY);
                worstPhase = Math.Max(worstPhase, Math.Abs(LifeShaderPort.PhaseOf(u, v) - phaseTwin));
                samples++;
            }

            // Row 0 (the uniform off): the plain look and the blend returns the read.
            Upload(side, seed, scale, cellsPer, 0.3f, 17, false);
            float[] zw = new float[4];
            float[] zo = new float[4];
            LifeShaderPort.LifeLookAt(0.3f, 0.6f, zw, zo);
            Check("row 0 (uniform off): the port's look is the plain one (w0 1, offsets 0) for seed " + seed + ", " + side + " texels",
                zw[0] == 1f && zw[1] == 0f && zo[0] == 0f && zo[3] == 0f, "");
        }

        Check("phase: the port's offsets equal the twin's at every sampled place (worst " + worstOff.ToString("G3") + ")", offsetsDiffer == 0,
            "differ " + offsetsDiffer + " of " + samples);
        Check("layer weights: the port's w0, w1 equal the twin's (worst " + worstW.ToString("G3") + ")", worstW < 1e-6, "");
        Check("blend: TwinValue equals the port's blend at every sample (worst " + worstBlend.ToString("G3") + ")", worstBlend < 1e-5, "");
        Check("score blend: BlendScore equals the port's blend with mean 0 (worst " + worstScore.ToString("G3") + ")", worstScore < 1e-5, "");
        Check("samples near a layer's phase wrap (weight near 0): " + nearWrap + ", clock fractions that round to 1: " + nearOne,
            nearWrap > 100 && nearOne > 100, "");
        Check("phase: the port's noise through the equaliser equals CloudLifecycle.Phase (worst " + worstPhase.ToString("G3") + ")",
            worstPhase < 1e-5, "");

        // The equaliser: the port reads the nine float4s, and equals CloudLifecycle.Equalise over the raw range.
        Upload(384, 12345, 1, 2, 0.5f, 3, true);
        double eqWorst = 0.0;
        for (int i = 0; i <= 1000; i++)
        {
            float raw = i / 1000f;
            eqWorst = Math.Max(eqWorst, Math.Abs(LifeShaderPort.EqualiseOf(raw) - CloudLifecycle.Equalise(raw)));
        }
        Check("equaliser: the port's nine float4s read as CloudLifecycle.Equalise over 0..1 (worst " + eqWorst.ToString("G3") + ")",
            eqWorst < 1e-6, "");

        List<string> outLines = new List<string>(lines);
        outLines.Add("samples: " + samples + " (2 seeds x 2 map sizes x 1600; near wrap " + nearWrap + ", near one " + nearOne + ")");
        return outLines.ToArray();
    }
}
'@

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("test-lifeshader-" + [Guid]::NewGuid().ToString("N") + ".cs")
Set-Content -Path $tmp -Value $tests -Encoding UTF8
try {
    Add-Type -Path @($src, $tmp)
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

$out = [LifeShaderTests]::Run()
$failed = 0
foreach ($line in $out) {
    Write-Host $line
    if ($line.StartsWith("FAIL")) { $failed++ }
}

Write-Host ""
if ($failed -gt 0) { Write-Host "$failed check(s) FAILED"; exit 1 }
Write-Host "all checks passed"
