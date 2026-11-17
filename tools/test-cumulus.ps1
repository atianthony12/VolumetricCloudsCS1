# Offline checks of the Cumulus cloud style (Sky/CloudStyle) and its noise (Sky/CumulusNoise3D,
# made by Sky/WorleyVolume), run against the BUILT mod DLL -- no game needed.
#
#   dotnet build VolumetricClouds/VolumetricClouds.csproj
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-cumulus.ps1
#
# What it guards: the noise is part of what a city's seed reproduces (invariant 14), so it must be
# the same bytes whatever the thread count, tile without a seam and keep its mean down its mips;
# the style's numbers are EVE-Redux V5's (their cumulus curve, evaluated as Unity does, and the
# cubics the shader evaluates must agree with it); the tallest cloud is where the curve says; the
# light gain keeps a sunlit face as bright as Classic's; and the coverage table (the author's "the
# same amount of cloud") runs from nothing to more, never back.

param(
    [string]$Dll = "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Mods\VolumetricClouds\VolumetricClouds.dll"
)

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Reflection;
public static class CumulusResolver
{
    public static string[] Dirs;
    [ThreadStatic] static bool busy;
    public static void Install() { AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        if (busy) return null;
        busy = true;
        try
        {
            string name = new AssemblyName(e.Name).Name;
            foreach (string dir in Dirs)
            {
                string path = Path.Combine(dir, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        }
        finally { busy = false; }
    }
}

public static class CumulusStats
{
    // Mean |v(x) - v(x+1)| over each y-z slice; the last one is across the wrap (n-1 -> 0).
    public static double[] Profile(byte[] v, int n)
    {
        double[] s = new double[n];
        for (int x = 0; x < n; x++)
        {
            double sum = 0; int x2 = (x + 1) % n;
            for (int z = 0; z < n; z++)
                for (int y = 0; y < n; y++) { int row = y * n + z * n * n; sum += Math.Abs(v[row + x] - v[row + x2]); }
            s[x] = sum / ((double)n * n);
        }
        return s;
    }

    public static double[] Stats(byte[] v)
    {
        double sum = 0; int min = 255, max = 0;
        foreach (byte b in v) { sum += b; if (b < min) min = b; if (b > max) max = b; }
        return new double[] { sum / v.Length, min, max };
    }

    public static bool Same(byte[][] a, byte[][] b)
    {
        if (a.Length != b.Length) return false;
        for (int l = 0; l < a.Length; l++)
        {
            if (a[l].Length != b[l].Length) return false;
            for (int i = 0; i < a[l].Length; i++) if (a[l][i] != b[l][i]) return false;
        }
        return true;
    }
}
"@
[CumulusResolver]::Dirs = @($managed, (Split-Path -Parent $Dll))
[CumulusResolver]::Install()

[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$gen = $asm.GetType("VolumetricClouds.Sky.CumulusNoise3D")
$style = $asm.GetType("VolumetricClouds.Sky.CloudStyle")
$failed = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { "  ok   $what" } else { "FAIL $what"; $script:failed++ }
}
function Value([string]$name) { return $style.GetField($name).GetValue($null) }
# By name and argument count: SunLight has an overload with sunlight through the clouds.
function Call([string]$name, [object[]]$arguments) {
    $m = @($style.GetMethods() | Where-Object { $_.Name -eq $name -and $_.GetParameters().Count -eq $arguments.Count })[0]
    return $m.Invoke($null, $arguments)
}

$size = [int]$gen.GetField("Size").GetValue($null)
$seedFor = $gen.GetMethod("SeedFor")
$generate = $gen.GetMethod("Generate")
$seed = [int]$seedFor.Invoke($null, [object[]]@(67890))

"=== CumulusNoise3D.Generate($size, seed, threads) ==="
$sw = [Diagnostics.Stopwatch]::StartNew()
$one = [byte[][]]$generate.Invoke($null, [object[]]@($size, $seed, 1))
$tOne = $sw.Elapsed.TotalMilliseconds
$sw.Restart()
$many = [byte[][]]$generate.Invoke($null, [object[]]@($size, $seed, 0))
$tMany = $sw.Elapsed.TotalMilliseconds
"  one thread: $([int]$tOne) ms, all cores ($([Environment]::ProcessorCount), capped at 8): $([int]$tMany) ms"

Check ([CumulusStats]::Same($one, $many)) "the same bytes on one thread and on every core (a seed reproduces it)"
Check ($one.Length -eq 8) "8 mip levels for 128^3 (got $($one.Length))"
$sizesOk = $true
for ($l = 0; $l -lt $one.Length; $l++) { $e = [int][Math]::Pow($size -shr $l, 3); if ($one[$l].Length -ne $e) { $sizesOk = $false } }
Check $sizesOk "each level is half the edge of the one above, down to 1^3"

$s0 = [CumulusStats]::Stats($one[0])
"  level 0: mean $($s0[0].ToString('F1')), min $($s0[1]), max $($s0[2])"
Check (($s0[1] -eq 0) -and ($s0[2] -eq 255)) "level 0 spans the whole 0..255 (min/max normalised, as EVE's)"
Check (($s0[0] -gt 120) -and ($s0[0] -lt 200)) "level 0's mean is puffy spheres, not empty or solid (got $($s0[0].ToString('F1')); the preview's EVE noise had 0.63 x 255 = 162)"

$meansOk = $true
for ($l = 1; $l -lt $one.Length; $l++) { $m = ([CumulusStats]::Stats($one[$l]))[0]; if ([Math]::Abs($m - $s0[0]) -gt 1.5) { $meansOk = $false; "  level $l mean $($m.ToString('F1'))" } }
Check $meansOk "every mip level keeps the mean (box filter): distant billows fade to their average"

$profile = [CumulusStats]::Profile($one[0], $size)
$inner = $profile[0..($size - 2)]
$m = ($inner | Measure-Object -Average).Average
$sd = [Math]::Sqrt((($inner | ForEach-Object { ($_ - $m) * ($_ - $m) }) | Measure-Object -Sum).Sum / $inner.Count)
$wrap = $profile[$size - 1]
$z = ($wrap - $m) / $sd
"  mean step between neighbours per slice: $($m.ToString('F2')) +- $($sd.ToString('F2')); across the wrap $($wrap.ToString('F2')) (z = $($z.ToString('F1')))"
Check ([Math]::Abs($z) -lt 3.0) "it tiles without a seam: the slice across the wrap is like any other slice"

$other = [byte[][]]$generate.Invoke($null, [object[]]@($size, [int]$seedFor.Invoke($null, [object[]]@(12345)), 0))
Check (-not [CumulusStats]::Same($one, $other)) "another city's seed gives another volume"

$detailGen = $asm.GetType("VolumetricClouds.Sky.CloudDetail3D")
$detailLevels = [byte[][]]$detailGen.GetMethod("Generate").Invoke($null, [object[]]@(128, [int]$detailGen.GetMethod("SeedFor").Invoke($null, [object[]]@(67890)), 0))
Check (-not [CumulusStats]::Same($one, $detailLevels)) "it is not cloud detail's texture (its own seed and octaves)"

Check ($tMany -lt 15000) "quick enough for a city load's worker thread here (<15 s; Mono in the game is slower)"

""
"=== CloudStyle: EVE's cumulus coverage curve ==="
$t = [single[]](Value "CurveTime"); $v = [single[]](Value "CurveValue")
$curveOk = $true
for ($i = 0; $i -lt $t.Length; $i++) {
    $got = [single](Call "Curve" @([single]$t[$i]))
    if ([Math]::Abs($got - $v[$i]) -gt 1e-5) { $curveOk = $false; "  key $i at $($t[$i]): $got, not $($v[$i])" }
}
Check $curveOk "the curve passes through every key (Gurdamma's Cumulus: -0.004 at 0.017, 0.976 at 0.087, -0.007 at 0.973)"
Check ([single](Call "Curve" @([single]0.0)) -eq $v[0] -and [single](Call "Curve" @([single]1.0)) -eq $v[$v.Length - 1]) "outside the keys it holds the end values, as Unity's float curves do"

# The shader evaluates the two segments as cubics; they must be the curve.
$cubicOk = $true
for ($k = 0; $k -lt $t.Length - 1; $k++) {
    $c = [single[]](Call "SegmentCubic" @([int]$k))
    for ($j = 0; $j -le 20; $j++) {
        $s = $j / 20.0
        $h = $t[$k] + ($t[$k + 1] - $t[$k]) * $s
        $cubic = (($c[0] * $s + $c[1]) * $s + $c[2]) * $s + $c[3]
        $ref = [single](Call "Curve" @([single]$h))
        if ([Math]::Abs($cubic - $ref) -gt 2e-4) { $cubicOk = $false; "  segment $k at s=$s : cubic $cubic, curve $ref" }
    }
}
Check $cubicOk "the shader's cubics (SegmentCubic) are the curve, both segments, end to end"

# A Hermite through Unity's keys, computed here independently of the mod.
$tin = [single[]](Value "CurveIn"); $tout = [single[]](Value "CurveOut")
function Hermite([double]$x) {
    $i = 1; while ($i -lt $t.Length - 1 -and $x -gt $t[$i]) { $i++ }
    $dt = $t[$i] - $t[$i - 1]; $s = ($x - $t[$i - 1]) / $dt
    $m0 = $tout[$i - 1] * $dt; $m1 = $tin[$i] * $dt
    return (2 * $s * $s * $s - 3 * $s * $s + 1) * $v[$i - 1] + ($s * $s * $s - 2 * $s * $s + $s) * $m0 + (-2 * $s * $s * $s + 3 * $s * $s) * $v[$i] + ($s * $s * $s - $s * $s) * $m1
}
$hermiteOk = $true
foreach ($x in 0.03, 0.05, 0.1, 0.2, 0.3, 0.5, 0.7, 0.9, 0.95) {
    $a = Hermite $x; $b = [single](Call "Curve" @([single]$x))
    if ([Math]::Abs($a - $b) -gt 1e-4) { $hermiteOk = $false; "  h=$x : Hermite $a, mod $b" }
}
Check $hermiteOk "it is Unity's Hermite between the keys (checked against an independent computation)"

$span = [single](Value "Span"); $tallest = [single](Value "Tallest"); $cmax = [single](Value "CoverageMax")
$atTop = [single](Call "Curve" @([single]($tallest / $span)))
"  tallest cloud $($tallest.ToString('F0')) m above the base; curve there $($atTop.ToString('F4')) (1 - most coverage = $((1 - $cmax).ToString('F4')))"
Check ([Math]::Abs($atTop - (1 - $cmax)) -lt 1e-3) "the tallest cloud is where the curve falls to 1 - the most coverage"
$above = [single](Call "Density" @([single]$cmax, [single](($tallest + 5) / $span), [single]1.0))
Check ($above -eq 0) "above it there is no cloud even at the most coverage and the strongest noise (the march may stop there)"
$inside = [single](Call "Density" @([single]$cmax, [single]0.2, [single]1.0))
Check ($inside -eq 1) "deep inside, at the most coverage and a puff's core, the density is full (a surface, not a haze)"
$monoOk = $true; $prev = -1.0
foreach ($n in 0.0, 0.2, 0.4, 0.6, 0.8, 1.0) { $d = [single](Call "Density" @([single]0.3, [single]0.2, [single]$n)); if ($d -lt $prev) { $monoOk = $false }; $prev = $d }
Check $monoOk "more noise, never less cloud: the noise's puffs are the clouds"

""
"=== CloudStyle: the light ==="
function HG([double]$c, [double]$g) { return (1 - $g * $g) / [Math]::Pow(1 + $g * $g - 2 * $g * $c, 1.5) }
$gain = [single](Value "LightGain")
$single0 = 0.1 * (HG 0 0.95) + 0.2 * (HG 0 0.8)
$multiple0 = 3.0 * (HG 0 0.2) + 0.3 * (HG 0 (-0.4))
$old = 0.65 + 0.35 * [Math]::Min((HG 0 0.55), 5.0)
"  side-on: single $($single0.ToString('F4')), multiple $($multiple0.ToString('F4')), gain $($gain.ToString('F4')), Classic's light $($old.ToString('F4'))"
Check ([Math]::Abs($gain * ($single0 + $multiple0) - $old) -lt 1e-4) "gain x EVE's lobes = Classic's light on a sunlit face seen side-on (brightness settings keep their meaning)"
$lit = [single](Call "SunLight" @([single]0.0, [single]0.0))
Check ([Math]::Abs($lit - $old) -lt 1e-4) "a sunlit surface side-on (no cloud towards the sun) gets exactly that"
$rim = [single](Call "SunLight" @([single]1.0, [single]0.0))
$cap = [single](Value "SingleCap")
"  straight into the sun, no cloud in the way: $($rim.ToString('F2')) (single lobes capped at $cap)"
Check ($rim -lt 5) "straight into the sun the silver lining is bright but cannot blow out (< 5 x a side-lit face)"
$deep = [single](Call "SunLight" @([single]0.0, [single]10.0))
Check (($deep -gt 0) -and ($deep -lt 0.02 * $old)) "ten optical depths in, next to no sunlight is left"

""
"=== LightThrough: sunlight through the clouds ==="
$through = $asm.GetType("VolumetricClouds.Sky.LightThrough")
function Through([string]$name, [object[]]$arguments) { return $through.GetMethod($name).Invoke($null, $arguments) }
$soakLight = $style.GetMethod("SunLight", [Type[]]@([single], [single], [single], [single]))
function Soaked([double]$cos, [double]$tau, [double]$amount, [double]$k) { return [single]$soakLight.Invoke($null, [object[]]@([single]$cos, [single]$tau, [single]$amount, [single]$k)) }
$k100 = [single](Through "KFor" @([single]1.0))
Check (([single](Through "AmountFor" @([single]0.0)) -eq 0) -and ([single](Through "AmountFor" @([single]1.0)) -eq 1) -and ([single](Through "AmountFor" @([single]2.0)) -eq 1)) "the slider: 0% off, all of the tail from 100% up"
Check (([Math]::Abs($k100 - 0.15) -lt 1e-6) -and ([Math]::Abs([single](Through "KFor" @([single]2.0)) - 0.075) -lt 1e-6)) "k: 0.15 at 100% (the look chosen), half at 200% (the function: since 2026-11 the slider holds this part at 100%)"
$offSame = $true; $neverDarker = $true
foreach ($tau in 0, 0.5, 2, 5, 10, 20, 40, 80) {
    foreach ($cos in -0.8, 0.0, 0.7, 1.0) {
        $plain = [single](Call "SunLight" @([single]$cos, [single]$tau))
        if ((Soaked $cos $tau 0 $k100) -ne $plain) { $offSame = $false }
        if ((Soaked $cos $tau 1 $k100) -lt $plain) { $neverDarker = $false }
    }
}
Check $offSame "0% is the light from before, exactly"
Check $neverDarker "never darker than the light from before"
Check ([Math]::Abs((Soaked 0 0 1 $k100) - $old) -lt 1e-5) "a sunlit face (tau 0) is unchanged: the brightness settings keep their meaning"
$glow = (Soaked 0 10 1 $k100) / $old
$thick = (Soaked 0 60 1 $k100) / $old
"  side-on, against a sunlit face: tau 10 $($glow.ToString('P1')) (before $(($deep / $old).ToString('P2'))), tau 60 $($thick.ToString('P2'))"
Check ($glow -gt 0.1) "ten optical depths in, a good share of the sun soaks through (the heaps glow)"
Check ($thick -lt 0.02) "sixty in -- a thick deck's far side -- next to none does (the backlit deck stays dark)"

""
"=== CloudShade: the light in the shade ==="
$shade = $asm.GetType("VolumetricClouds.Sky.CloudShade")
function Shade([string]$name, [object[]]$arguments) { return [single]$shade.GetMethod($name).Invoke($null, $arguments) }
Check (((Shade "SkyFloorFor" @([single]0)) -eq [single]0.45) -and ((Shade "OcclusionScaleFor" @([single]0)) -eq 1) -and ((Shade "GroundFor" @([single]0, [single]0.64, [single]0.85)) -eq 0)) "0%: the sky light at the base 45% of the top's, the occlusion as it was, no ground light -- the light from before"
Check (([Math]::Abs((Shade "SkyFloorFor" @([single]1)) - 0.75) -lt 1e-6) -and ([Math]::Abs((Shade "OcclusionScaleFor" @([single]1)) - 0.5) -lt 1e-6)) "100%: the base 75% of the top's, half the occlusion (the look chosen)"
Check (((Shade "SkyFloorFor" @([single]2)) -eq 1) -and ((Shade "OcclusionScaleFor" @([single]2)) -eq 0)) "200%: the base as lit as the top, no occlusion -- never more (the function: since 2026-11 the slider holds this part at 100%)"
$noon = Shade "GroundFor" @([single]1, [single]0.64, [single]0.855)
$low = Shade "GroundFor" @([single]1, [single]1.0, [single]0.139)
"  the ground's light on the base at 100%, as a share of the sun: noon at 64% $($noon.ToString('F3')), an 8 deg sun at 100% $($low.ToString('F3'))"
Check ([Math]::Abs($noon - 0.25 * (1 - 0.6 * 0.64) * 0.855) -lt 1e-5) "the ground's light: albedo 0.25 x the ground in sun x the sun's height"
Check ($low -lt 0.02) "at a low sun under a full sky next to none: the backlit deck at sunset is left alone"
Check ((Shade "GroundFor" @([single]1, [single]0.5, [single]-0.1)) -eq 0) "none once the sun is down"

""
"=== DeepLight: light through clouds ==="
$deepType = $asm.GetType("VolumetricClouds.Sky.DeepLight")
function Deep([string]$name, [object[]]$arguments) { return [single]$deepType.GetMethod($name).Invoke($null, $arguments) }
function DeepValue([string]$name) { return [single]$deepType.GetField($name).GetValue($null) }
$split = @(0, 0.5, 1, 1.5, 2, 2.5) | ForEach-Object { Deep "ForSlider" @([single]$_) }
Check (($split[0] -eq 0) -and ($split[1] -eq 0) -and ($split[2] -eq 0) -and ([Math]::Abs($split[3] - 0.5) -lt 1e-6) -and ($split[4] -eq 1) -and ($split[5] -eq 1)) "on the Light inside the clouds slider: none up to 100% (every player's value at the default), half at 150%, all at 200%"
$taus = 0, 0.5, 2, 5, 10, 40
$offOk = $true; $faceOk = $true; $underOk = $true
foreach ($tau in $taus) {
    $before = [single][Math]::Exp(-$tau / 2)
    if ((Deep "Scattered" @([single]$tau, $before, [single]0)) -ne $before) { $offOk = $false }
    if ((Deep "Scattered" @([single]$tau, $before, [single]1)) -lt $before) { $underOk = $false }
}
foreach ($s in 0.5, 1, 2) { if ([Math]::Abs((Deep "Scattered" @([single]0, [single]1, [single]$s)) - 1) -gt 1e-6) { $faceOk = $false } }
Check $offOk "0%: the scattered light is the light from before, exactly"
Check $faceOk "the scattered light at tau 0 is unchanged, before the edge"
Check $underOk "the scattered light is never under the light before, before the edge"
$half = Deep "Scattered" @([single]5, [single]0, [single]0.5)
$full = Deep "Scattered" @([single]5, [single]0, [single]1)
$double = Deep "Scattered" @([single]5, [single]0, [single]2)
Check (($half -le $full) -and ($full -le $double)) "it grows with the setting: 50% <= 100% <= 200% at tau 5 ($($half.ToString('F3')), $($full.ToString('F3')), $($double.ToString('F3')))"
$at10 = Deep "Scattered" @([single]10, [single]0, [single]1)
$at60 = Deep "Scattered" @([single]60, [single]0, [single]1)
"  at 100%: tau 10 $($at10.ToString('F3')) (before $([Math]::Exp(-5).ToString('F4'))), tau 60 $($at60.ToString('F3'))"
Check ($at10 -gt 0.4) "at 100%, ten optical depths in, over 0.4 of the scattered sun (the reference: about half)"
Check ($at60 -lt 0.2) "at 100%, sixty in, under 0.2 (a thick deck's far side stays dim)"
$floor = DeepValue "EdgeFloor"; $beamCap = DeepValue "BeamCap"
Check (((Deep "EdgeFor" @([single]0)) -eq 1) -and ([Math]::Abs((Deep "EdgeFor" @([single]1)) - $floor) -lt 1e-6) -and ([Math]::Abs((Deep "EdgeFor" @([single]2)) - $floor) -lt 1e-6)) "the edge: 1 at 0%, EdgeFloor ($floor) from 100% on"
Check (((Deep "CapFactor" @([single]0)) -eq 1) -and ([Math]::Abs((Deep "CapFactor" @([single]1)) - $beamCap) -lt 1e-6)) "the direct beam's cap: x1 at 0%, x BeamCap ($beamCap) at 100%"
Check (([Math]::Abs((Deep "MidLobe" @([single]0))) -lt 1e-6) -and ((Deep "MidLobe" @([single]-1)) -eq 0) -and ((Deep "MidLobe" @([single]1)) -gt 0)) "the middle lobe: nothing side-on or away from the sun, something towards it"
Check ((Deep "MidDepth" @([single]0)) -eq 0) "the middle lobe: none at a cloud's face (tau 0)"
Check (((Deep "FillFor" @([single]0)) -eq 0) -and ((Deep "MidFor" @([single]0)) -eq 0)) "0%: the slow light and the middle lobe are off (the shader's branches not taken)"

""
"=== SunsetLight: the clouds keep the setting sun ==="
$sunset = $asm.GetType("VolumetricClouds.Sky.SunsetLight")
function Sunset([string]$name, [object[]]$arguments) { return [single]$sunset.GetMethod($name).Invoke($null, $arguments) }
function SunsetShare([single]$el, [single]$h, [single]$s) {
    $a = [object[]]@($el, $h, $s, [single]0, [single]0, [single]0)
    [void]$sunset.GetMethod("Share").Invoke($null, $a)
    return ,@([single]$a[3], [single]$a[4], [single]$a[5])
}
$hold = [single]$sunset.GetField("HoldElevation").GetValue($null)
Check ([Math]::Abs((Sunset "DayTime" @([single]$hold)) - 1) -lt 2e-3 -and (Sunset "DayTime" @([single]2)) -lt 0.99) "the hold elevation is where the game's DayTime leaves 1"
# The game's DayTime and sun path against his log (2026-10-04): 1.3 deg -> 0.846, -1.8 -> 0.592; 17.90h -> 1.3 deg.
# The log rounds the elevation to 0.1 deg, and DayTime moves ~0.085 a degree there: 0.005 of slack.
Check ([Math]::Abs((Sunset "DayTime" @([single]1.3)) - 0.846) -lt 5e-3 -and [Math]::Abs((Sunset "DayTime" @([single]-1.8)) - 0.592) -lt 5e-3) "DayTime is the game's (his log: 0.846 at 1.3 deg, 0.592 at -1.8)"
Check ([Math]::Abs((Sunset "TimeAt" @([single]1.3, [single]29, $true)) - 17.90) -lt 0.01 -and [Math]::Abs((Sunset "TimeAt" @([single]3.16, [single]29, $false)) - 6.24) -lt 0.01) "the game's sun path: 1.3 deg at 17.90h (his log), 3.16 deg at 6.24h in the morning (the gradient's key)"
$dip = Sunset "Dip" @([single]1100)
Check ([Math]::Abs($dip - 1.064) -lt 0.01) "a cloud 1100 m up sees the sun 1.06 deg under the horizontal (got $($dip.ToString('F3')))"
$up = SunsetShare $hold 1300 1
Check ($up[0] -eq 1 -and $up[1] -eq 1 -and $up[2] -eq 1) "at the hold elevation the clouds get the hold light: the game's light there, no step"
$prev = SunsetShare 3 1300 1; $redder = $true; $falls = $true
foreach ($el in 2, 1, 0, -0.5, -1, -1.5) {
    $sh = SunsetShare $el 1300 1
    if (($sh[1] / $sh[0]) -gt ($prev[1] / $prev[0]) + 1e-6 -or ($sh[2] / $sh[0]) -gt ($prev[2] / $prev[0]) + 1e-6) { $redder = $false }
    if ($sh[0] -gt $prev[0] + 1e-6) { $falls = $false }
    $prev = $sh
}
Check $redder "lower and lower, the light turns redder (green and blue go first)"
Check $falls "and never brighter"
$m1 = SunsetShare -1 1300 1
"  at -1 deg, 1300 m up: ($($m1[0].ToString('F2')),$($m1[1].ToString('F2')),$($m1[2].ToString('F3'))) of the hold light"
Check ($m1[0] -gt 0.6 -and $m1[1] -lt 0.5 * $m1[0] -and $m1[2] -lt 0.1 * $m1[0]) "a degree under the horizon the cloud is still lit, and red"
$edgeGone = Sunset "Edge" @([single](-0.85 - 1.064 - 0.01), [single]1100, [single]1)
$edgeTop = Sunset "Edge" @([single](-0.85 - 1.064 - 0.01), [single]2000, [single]1)
Check ($edgeGone -eq 0 -and $edgeTop -gt 0.5) "the Earth's shadow: when the sun has gone for the base (1100 m) a top at 2000 m is still lit"
Check ((Sunset "Edge" @([single]0, [single]0, [single]1)) -eq 1 -and (Sunset "Edge" @([single]-0.9, [single]0, [single]1)) -eq 0) "on the ground: lit with the sun on the horizon, dark once its disc is gone (refraction kept)"
$s2 = SunsetShare -1 1300 2
Check ($s2[0] -gt $m1[0]) "200%: as if the clouds stood twice as high -- more light, for longer"
Check ((Sunset "AmountFor" @([single]0)) -eq 0) "0%: off, the game's sun as before"

""
"=== The weather map's normal scores (CloudDensityField.ScoreBytes): the same for every city ==="
$fieldType = $asm.GetType("VolumetricClouds.Sky.CloudDensityField")
$quantile = $fieldType.GetMethod("NormalQuantile")
$range = [single]$fieldType.GetField("ScoreRange").GetValue($null)
$qOk = $true
foreach ($pair in @(@(0.5, 0.0), @(0.9, 1.2815516), @(0.975, 1.959964), @(0.01, -2.326348), @(0.999, 3.090232))) {
    $got = [single]$quantile.Invoke($null, [object[]]@([single]$pair[0]))
    if ([Math]::Abs($got - $pair[1]) -gt 2e-4) { $qOk = $false; "  NormalQuantile($($pair[0])) = $got, not $($pair[1])" }
}
Check $qOk "NormalQuantile is the standard normal's (0.5 -> 0, 0.9 -> 1.2816, 0.975 -> 1.96, 0.01 -> -2.33)"
$topTenth = [single](Value "TopTenthScore")
Check ([Math]::Abs($topTenth - [single]$quantile.Invoke($null, [object[]]@([single]0.9))) -lt 1e-4) "TopTenthScore is the normal score a tenth of a map lies above"

$any = [Reflection.BindingFlags]"Instance,Public,NonPublic"
$shareOk = $true; $orderOk = $true
foreach ($fs in 12345, 23456, 34567) {
    $f = [Activator]::CreateInstance($fieldType, [object[]]@($fs))
    $scores = [byte[]]$fieldType.GetMethod("ScoreBytes").Invoke($f, @())
    $density = [single[]]$fieldType.GetField("_density", $any).GetValue($f)
    $cut = [int](($topTenth + $range) / (2 * $range) * 255 + 0.5)
    $above = 0; $sum = 0.0
    foreach ($b in $scores) { if ($b -ge $cut) { $above++ }; $sum += $b }
    $share = $above / $scores.Length
    "  map $fs : $($share.ToString('P1')) of it at or above the top-tenth score, mean score byte $(($sum / $scores.Length).ToString('F1'))"
    if ([Math]::Abs($share - 0.1) -gt 0.012) { $shareOk = $false }
    # Rank order: a denser texel never gets a lower score (checked on a spread of pairs).
    $idx = [int[]](0..($density.Length - 1) | Where-Object { $_ % 97 -eq 0 })
    $sorted = [int[]]($idx | Sort-Object { $density[$_] })
    for ($i = 1; $i -lt $sorted.Length; $i++) { if ($scores[$sorted[$i]] -lt $scores[$sorted[$i - 1]]) { $orderOk = $false; break } }
}
Check $shareOk "on every map a tenth of it is at or above the top-tenth score (to 1.2%): the same share for every city"
Check $orderOk "a denser texel never has a lower score: the score is the value's rank"

$line = [single[]]$style.GetMethod("Line", [Type[]]@([single])).Invoke($null, [object[]]@([single]0.1))
$outA = [single]0; $outB = [single]0
$lineArgs = [object[]]@([single]0.1, $outA, $outB)
[void]$style.GetMethod("Line", [Type[]]@([single], [single].MakeByRefType(), [single].MakeByRefType())).Invoke($null, $lineArgs)
Check (($lineArgs[1] -eq $line[0]) -and ($lineArgs[2] -eq $line[1])) "the shader's allocation-free Line gives the same line"
$cubics = [single[][]](Value "Cubics")
Check ((@(0..3 | Where-Object { $cubics[0][$_] -ne ([single[]](Call "SegmentCubic" @([int]0)))[$_] })).Count -eq 0) "the cached cubics are SegmentCubic's"
$tMedian = 0.5
$tTop = ($topTenth + $range) / (2 * $range)
Check ([Math]::Abs(($line[0] + $line[1] * $tMedian) - 0.1) -lt 1e-5) "the coverage line gives the table's value at the map's median"
Check ([Math]::Abs(($line[0] + $line[1] * $tTop) - (0.1 + [single](Value "CoverageSlope"))) -lt 1e-5) "and CoverageSlope more at its top tenth"

""
"=== CloudStyle: the heaps stop at HeapCap, the layer's thickness follows the map ==="
$cap = [single](Value "HeapCap")
$capOk = $true
foreach ($cv in 0.3, 0.6, 0.85, 0.9, 1.0) {
    $expect = [single](Call "EdgeCoverage" @([single][Math]::Min($cv, $cap)))
    if ([single](Call "HeapEdge" @([single]$cv)) -ne $expect) { $capOk = $false; "  HeapEdge($cv) wrong" }
}
Check $capOk "the heaps' coverage is the table's up to $($cap * 100)% and holds there (no more heaps: their repeat never fills the sky)"
$variety = [single](Value "LayerThicknessVariety")
$tMid = [single]0.5
$tTopTenth = [single](($topTenth + $range) / (2 * $range))
Check ([Math]::Abs([single](Call "LayerFactor" @($tMid)) - 1) -lt 1e-5) "at the map's median the layer is Layer thickness exactly"
Check ([Math]::Abs([single](Call "LayerFactor" @($tTopTenth)) - (1 + $variety * $topTenth)) -lt 1e-4) "at its top tenth, (1 + $variety x 1.28) of it: thick where the map is high"
$thin = [single](Value "LayerThinnest"); $thick = [single](Value "LayerThickest")
Check (([single](Call "LayerFactor" @([single]0)) -eq $thin) -and ([single](Call "LayerFactor" @([single]1)) -eq $thick)) "never thinner than $thin x or thicker than $thick x"
$cachedLine = [single[]](Value "LayerLineCached")
$freshLine = [single[]](Call "LayerLine" @())
Check (($cachedLine[0] -eq $freshLine[0]) -and ($cachedLine[1] -eq $freshLine[1])) "the cached layer line the shader parameters use is LayerLine's"

# Under the type (Mixed, Auto): the slab reaches the tallest heap at the heaps' coverage now (CloudStyle.TypedHeapTop).
$tallestHeap = [single](Value "Tallest")
$covMax = [single](Value "CoverageMax")
Check ([single](Call "HeapTopAt" @($covMax)) -eq $tallestHeap) "a heap at the most coverage ends at Tallest"
Check ([single](Call "HeapTopAt" @([single]1)) -eq $tallestHeap) "more coverage than that never asks for more"
$topsOk = $true; $prevTop = 0.0
for ($i = 0; $i -le 50; $i++) {
    $cv = [single]($i * 0.01)
    $top = [single](Call "HeapTopAt" @($cv))
    if ($top -lt $prevTop) { $topsOk = $false; "  HeapTopAt falls at $cv" }
    # Above the top the coverage curve leaves no cloud at all, so the slab never clips a heap.
    $hAbove = [single](($top + 1) / [single](Value "Span"))
    if ($cv -le $covMax -and $cv -gt 0.03 -and ($cv + [single](Call "Curve" @($hAbove)) - 1) -gt 1e-3) { $topsOk = $false; "  a heap of $cv reaches over HeapTopAt" }
    $prevTop = $top
}
Check $topsOk "the tallest heap only rises with its coverage, and none reaches over it"

""
"=== CloudStyle: Rounded cumulus bases (CumulusBases, 2026-11-16) ==="
$shapeFor = $style.GetMethod("ShapeFor")
$eve = $style.GetField("EveShape").GetValue($null)
$s0 = $shapeFor.Invoke($null, @([single]0))
Check ([object]::ReferenceEquals($s0, $eve)) "at 0 the curve is EVE's own object: the shader gets the very numbers it always got"
$cub = [single[][]](Value "Cubics")
$same = $true
for ($k = 0; $k -lt 2; $k++) { for ($j = 0; $j -lt 4; $j++) { if ($eve.Cubics[$k][$j] -ne $cub[$k][$j]) { $same = $false } } }
for ($k = 0; $k -lt 3; $k++) { if ($eve.Time[$k] -ne ([single[]](Value "CurveTime"))[$k]) { $same = $false } }
if ($eve.End -ne ([single[]](Value "CurveValue"))[2]) { $same = $false }
Check $same "EVE's shape holds the cached cubics, the keys and the end value, number for number"
Check (($eve.Tallest -eq $tallestHeap) -and ([single]$eve.TopOf([single]$covMax) -eq $tallestHeap)) "EVE's shape ends where Tallest says"
$evalOk = $true
for ($i = 0; $i -le 200; $i++) { $h = [single]($i / 200.0); if ([single]$eve.Eval($h) -ne [single](Call "Curve" @($h))) { $evalOk = $false; "  EveShape.Eval($h) differs from Curve" } }
Check $evalOk "EVE's shape evaluates as Curve, everywhere"
$widest = [single]$style.GetField("BaseWidest").GetValue($null)
$v = [single[]](Value "CurveValue"); $t = [single[]](Value "CurveTime")
foreach ($b in 0.25, 0.5, 1.0) {
    $s = $shapeFor.Invoke($null, @([single]$b))
    $ok = $true
    if ($s.Time[0] -ne $t[0]) { $ok = $false; "  the foot moved" }
    if ([Math]::Abs([single]$s.Eval($s.Time[1]) - $v[1]) -gt 1e-5) { $ok = $false; "  the peak's value is not EVE's" }
    if ([single]$s.Eval([single]0) -ge 0.0 -or [single]$s.Eval($s.Time[0]) -ge 0.0) { $ok = $false; "  cloud at the base itself" }
    $prev = -1.0
    for ($i = 0; $i -le 400; $i++) {
        $h = $s.Time[0] + ($s.Time[1] - $s.Time[0]) * $i / 400.0
        $c = [single]$s.Eval([single]$h)
        if ($c -lt $prev - 1e-6) { $ok = $false; "  the climb falls at $h"; break }
        $prev = $c
    }
    # The fall: EVE's leaves its peak key with a tiny rise (its out tangent), then only falls.
    $prev = -1.0; $falling = $false
    for ($i = 0; $i -le 400; $i++) {
        $h = $s.Time[1] + ($s.Time[2] - $s.Time[1]) * $i / 400.0
        $c = [single]$s.Eval([single]$h)
        if ($c -gt $v[1] + 0.005) { $ok = $false; "  the fall rises over the peak at $h"; break }
        if ($c -lt $prev) { $falling = $true } elseif ($falling -and $c -gt $prev + 1e-6) { $ok = $false; "  the fall rises again at $h"; break }
        $prev = $c
    }
    $expectWidest = $t[1] + ([Math]::Max($widest, $t[1]) - $t[1]) * $b
    if ([Math]::Abs($s.Time[1] - $expectWidest) -gt 1e-6) { $ok = $false; "  the widest level is not where the row puts it" }
    # The slab: no heap reaches over its top at any coverage.
    for ($i = 3; $i -le 45; $i++) {
        $cv = [single]($i * 0.01)
        $hAbove = [single](($s.Tops[$i] + 1) / [single](Value "Span"))
        if (($cv + [single]$s.Eval($hAbove) - 1) -gt 1e-3) { $ok = $false; "  a heap of $cv reaches over its top at $b" }
    }
    # The climb is slower: the coverage that makes an underside is crossed over a taller stretch.
    $lo = 0.0; while ($lo -lt 1 -and [single]$s.Eval([single]$lo) -lt 0.55) { $lo += 0.0005 }
    $hi = 0.0; while ($hi -lt 1 -and [single]$s.Eval([single]$hi) -lt 0.95) { $hi += 0.0005 }
    $elo = 0.0; while ([single]$eve.Eval([single]$elo) -lt 0.55) { $elo += 0.0005 }
    $ehi = 0.0; while ([single]$eve.Eval([single]$ehi) -lt 0.95) { $ehi += 0.0005 }
    if (($hi - $lo) -le ($ehi - $elo)) { $ok = $false; "  the undersides' stretch is no taller than EVE's" }
    Check $ok ("at {0:P0}: the foot and peak value are EVE's, the climb only rises, the fall only falls, widest at {1:F3} of the span, no cloud under the base, every heap under its top; undersides formed over {2:F0} m of the span (EVE's {3:F0} m)" -f $b, $s.Time[1], (($hi - $lo) * 2000), (($ehi - $elo) * 2000))
}
$d0 = [single]$style.GetMethod("Density", [Type[]]@([single], [single], [single])).Invoke($null, @([single]0.3, [single]0.2, [single]0.8))
$d0b = [single]$style.GetMethod("Density", [Type[]]@([single], [single], [single], [single])).Invoke($null, @([single]0.3, [single]0.2, [single]0.8, [single]0))
Check ($d0 -eq $d0b) "the density at 0% is EVE's density"
Check ([single]$style.GetProperty("BasesNow").GetValue($null, $null) -eq 0) "offline the row is off: the tops and the slab are EVE's"

""
"=== CloudStyle: Softer cumulus (CumulusSoftness, 2026-11-16) ==="
$softer = $style.GetMethod("SofterDensity")
$dens3 = $style.GetMethod("Density", [Type[]]@([single], [single], [single]))
Add-Type -TypeDefinition @"
public static class SofterHelp
{
    // cg exactly as CloudStyle.Density works it out (float arithmetic): coverage + curve - 1, clamped.
    public static float Cg(float coverage, float curve) { float cg = coverage + curve - 1f; return cg < 0f ? 0f : cg > 1f ? 1f : cg; }
}
"@
$tallestNow = [single]2000
$same0 = $true
foreach ($cov in 0.05, 0.12, 0.2, 0.3, 0.45) {
    foreach ($h in 0.03, 0.06, 0.1, 0.3, 0.6) {
        $cg = [SofterHelp]::Cg([single]$cov, [single](Call "Curve" @([single]$h)))
        for ($i = 0; $i -le 100; $i++) {
            $n = [single]($i / 100.0)
            $old = [single]$dens3.Invoke($null, @([single]$cov, [single]$h, $n))
            foreach ($w in 0.0, 1.0) {
                $new = [single]$softer.Invoke($null, @([single]$cg, $n, [single]0.5, [single]$w, [single]300, [single]0, $tallestNow))
                if ($new -ne $old) { $same0 = $false; "  at 0%: cov $cov h $h noise $n wisps $w : $new vs $old" }
            }
        }
    }
}
Check $same0 "at 0% the density is EVE's, number for number (fray and fringe asked or not)"
$hardAt = $style.GetMethod("SoftHardnessAt"); $startAt = $style.GetMethod("SoftStartAt")
$k0 = [single]$hardAt.Invoke($null, @([single]0)); $k1 = [single]$hardAt.Invoke($null, @([single]1))
$kSoft = [single]$style.GetField("SoftHardness").GetValue($null)
Check (([Math]::Abs($k0 - 1 / (1 - [single](Value "Hardness"))) -lt 1e-3) -and ([Math]::Abs($k1 - $kSoft) -lt 1e-4)) ("the edge's hardness runs from EVE's {0:F2} at 0% to {1:F2} at 100%" -f $k0, $k1)
$s0 = [single]$startAt.Invoke($null, @([single]0)); $s1 = [single]$startAt.Invoke($null, @([single]1))
Check (($s0 -eq 0) -and ($s1 -lt 0)) ("the soft edge starts at EVE's at 0%, {0:F4} further out at 100%" -f (-$s1))
# At 100%, without the fray: a soft skin out, the half-density surface where EVE's was, the core as dense.
$ok = $true
$ero = [single](Value "ErosionDepth")
$halfOld = $null; $halfNew = $null
$cg = [single]0.3
for ($i = 0; $i -le 2000; $i++) {
    $n = [single](0.4 + 0.2 * $i / 2000.0)
    $inside = $cg - (1 - $n) * $ero
    $old = [single]$softer.Invoke($null, @($cg, $n, [single]0.5, [single]0, [single]300, [single]0, $tallestNow))
    $new = [single]$softer.Invoke($null, @($cg, $n, [single]0.5, [single]0, [single]300, [single]1, $tallestNow))
    if ($null -eq $halfOld -and $old -ge 0.5) { $halfOld = $inside }
    if ($null -eq $halfNew -and $new -ge 0.5) { $halfNew = $inside }
    if ($new -lt 0 -or $new -gt 1) { $ok = $false; "  out of 0..1 at $n" }
    if ($old -ge 1 -and $inside -gt (1 - $s1 * $k1) / $k1 + 1e-3 -and $new -lt 1) { $ok = $false; "  the core thinner at inside $inside" }
    if ($inside -lt 0 -and $inside -gt $s1 + 1e-3 -and $new -le 0) { $ok = $false; "  no skin outside the hard edge at inside $inside" }
}
Check ($ok -and [Math]::Abs($halfNew - $halfOld) -lt 2e-3) ("at 100%: a soft skin outwards, the core as dense, the half-density surface where EVE's was (inside {0:F4} vs {1:F4})" -f $halfNew, $halfOld)
# Nothing where cg is 0, and it thins to nothing as cg does (no cliff at a heap's top or foot).
$ok = ([single]$softer.Invoke($null, @([single]0, [single]1, [single]1, [single]1, [single]300, [single]1, $tallestNow)) -eq 0)
foreach ($cgs in 1e-5, 1e-4) { $d = [single]$softer.Invoke($null, @([single]$cgs, [single]1, [single]1, [single]1, [single]300, [single]1, $tallestNow)); if ($d -gt 0.01) { $ok = $false; "  $d at cg $cgs" } }
Check $ok "no cloud where cg is 0, and next to nothing just inside (the skin and the fringe come in with cg)"
# The fray carves (strands at the base, billows higher); the fringe never adds more than its own density.
# (Checked with SoftFrayBias at 0; with it, the fray gives back at most its depth x the bias, below.)
$fr = [single]$style.GetField("SoftFringe").GetValue($null)
$biasField = $style.GetField("SoftFrayBias"); $bias = [single]$biasField.GetValue($null); $biasField.SetValue($null, [single]0)
$ok = $true; $carved = $false
foreach ($n in 0.5, 0.55, 0.6, 0.7, 0.85) {
    foreach ($fine in 0.2, 0.5, 0.8, 1.0) {
        foreach ($y in 0, 300, 1500) {
            $plain = [single]$softer.Invoke($null, @($cg, [single]$n, [single]$fine, [single]0, [single]$y, [single]1, $tallestNow))
            $fray = [single]$softer.Invoke($null, @($cg, [single]$n, [single]$fine, [single]1, [single]$y, [single]1, $tallestNow))
            if ($fray -gt [Math]::Max($plain, $fr) + 1e-6) { $ok = $false; "  the fray added cloud at n $n fine $fine y $y" }
            if ($fray -lt $plain - 1e-4) { $carved = $true }
        }
    }
}
Check ($ok -and $carved) "without its bias the fray only carves; the fringe is at most its own density"
$biasField.SetValue($null, $bias)
$back = [single]$style.GetField("SoftFray").GetValue($null) * $bias * $k1
$ok = $true
foreach ($n in 0.5, 0.55, 0.6, 0.7, 0.85) {
    foreach ($fine in 0.2, 0.5, 0.8, 1.0) {
        $plain = [single]$softer.Invoke($null, @($cg, [single]$n, [single]$fine, [single]0, [single]300, [single]1, $tallestNow))
        $fray = [single]$softer.Invoke($null, @($cg, [single]$n, [single]$fine, [single]1, [single]300, [single]1, $tallestNow))
        if ($fray -gt [Math]::Max($plain + $back, $fr) + 1e-5) { $ok = $false; "  the fray gave back more than its bias at n $n fine $fine" }
    }
}
Check $ok ("with its bias ({0:F2}) the fray gives back at most depth x bias ({1:F3} of density)" -f $bias, $back)
Check ([single]$style.GetProperty("SoftnessNow").GetValue($null, $null) -eq 0) "offline the row is off"
Check ([single]$style.GetProperty("LineTrimNow").GetValue($null, $null) -eq [single]$style.GetProperty("BaseTrimNow").GetValue($null, $null)) "off, the heaps' line is trimmed as before (Rounded cumulus bases only)"

""
"=== CloudStyle: how much cloud (the same amount as Classic) ==="
$table = [single[]](Value "EdgeTable")
Check ($table.Length -eq 21) "one entry per 5% of the Intensity slider, 0% to 100% (got $($table.Length))"
$monotone = $true
for ($i = 1; $i -lt $table.Length; $i++) { if ($table[$i] -lt $table[$i - 1]) { $monotone = $false } }
Check $monotone "more intensity, never less cloud: the table never falls"
Check (($table[0] -ge -0.5) -and ($table[$table.Length - 1] -le $cmax)) "every entry is within what the shader's clamp can use"
$mid = [single](Call "EdgeCoverage" @([single]0.525))
$expect = ($table[10] + $table[11]) / 2
Check ([Math]::Abs($mid - $expect) -lt 1e-5) "between entries it is linear (52.5% = halfway from 50% to 55%)"
Check ([single](Call "EdgeCoverage" @([single]-1.0)) -eq $table[0] -and [single](Call "EdgeCoverage" @([single]2.0)) -eq $table[20]) "outside 0..100% it holds the ends"

""
if ($failed -eq 0) { "all cumulus checks pass" } else { "$failed cumulus check(s) FAILED"; exit 1 }
