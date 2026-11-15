# Offline checks of the lightning maths, run against the BUILT mod DLL -- no game needed.
#
#   dotnet build VolumetricClouds/VolumetricClouds.csproj
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-lightning.ps1
#
# Run it in a fresh process (as above): loaded assemblies stay locked in an existing session.
#
# What can be tested this way is any class with no Unity-NATIVE dependency: Vector3 and Mathf
# are plain managed code and work here, but anything touching Shader, Mesh, Material or a
# Singleton<> is an internal call into the engine and throws. That is why the flicker formula
# lives in LightningFlicker rather than in CloudLightning, whose static initialiser calls
# Shader.PropertyToID.
#
# Two PowerShell traps this script already stepped in:
#   - a script block used as the AssemblyResolve handler re-enters itself and overflows the
#     stack; the resolver below is compiled C#
#   - New-Object results are PSObject-wrapped and do not convert when passed through
#     MethodInfo.Invoke; unwrap them with .psobject.BaseObject

param(
    [string]$Dll = "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Mods\VolumetricClouds\VolumetricClouds.dll"
)

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Reflection;
public static class TestResolver
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
"@
[TestResolver]::Dirs = @($managed, (Split-Path -Parent $Dll))
[TestResolver]::Install()

[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
[void][System.Reflection.Assembly]::LoadFrom("$managed\ColossalManaged.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$flags = [System.Reflection.BindingFlags]"Static,NonPublic,Public"
$failed = 0

function Check([string]$what, [bool]$ok) {
    if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failed++ }
}

"=== LightningFlicker.Intensity(frame, start, reduction) ==="
# Two overloads: (frame, start, reduction) is the game's formula, the 4-argument one takes a profile.
$intensity = $asm.GetType("VolumetricClouds.Sky.LightningFlicker").GetMethods($flags) |
    Where-Object { $_.Name -eq "Intensity" -and $_.GetParameters().Count -eq 3 }
$start = [uint32]5000

function Level([int]$age, [single]$reduction) {
    [object[]]$a = [uint32]($start + $age), $start, $reduction
    return [single]$intensity.Invoke($null, $a)
}

$envelope = 0..29 | ForEach-Object { Level $_ 1.0 }
$raw = 0..29 | ForEach-Object { Level $_ 0.0 }
"  envelope (reduction 1): " + (($envelope | ForEach-Object { $_.ToString("F2") }) -join " ")
"  raw      (reduction 0): " + (($raw | ForEach-Object { $_.ToString("F2") }) -join " ")

$peakAge = 0; for ($i = 1; $i -lt 30; $i++) { if ($envelope[$i] -gt $envelope[$peakAge]) { $peakAge = $i } }
Check "envelope starts at 0" ($envelope[0] -eq 0)
Check "envelope peaks at ~1.0 a third of the way to frame 20 (peak $($envelope[$peakAge].ToString('F3')) at age $peakAge)" (($peakAge -ge 6) -and ($peakAge -le 7) -and ($envelope[$peakAge] -gt 0.98) -and ($envelope[$peakAge] -le 1.0001))
Check "envelope is gone from frame 20" ((($envelope[20..29] | Measure-Object -Maximum).Maximum) -eq 0)
Check "raw flicker stays within 0..1" ((($raw | Measure-Object -Minimum).Minimum -ge 0) -and (($raw | Measure-Object -Maximum).Maximum -le 1))
Check "raw flicker is held at 0.75 or more for ages 2..4" ((($raw[2..4] | Measure-Object -Minimum).Minimum) -ge 0.75)
Check "raw flicker reseeds every 3 frames (5010, 5011, 5012 all divide to 1670 and share a level; 5013 does not have to)" (($raw[10] -eq $raw[11]) -and ($raw[11] -eq $raw[12]))
Check "half reduction is the midpoint of the two" ([math]::Abs((Level 10 0.5) - (($raw[10] + $envelope[10]) / 2)) -lt 1e-5)

""
"=== LightningFlicker.Profile.Random + Intensity(frame, start, reduction, profile): our own strikes ==="
$flickerType = $asm.GetType("VolumetricClouds.Sky.LightningFlicker")
$profileType = $flickerType.GetNestedType("Profile")
$makeRandom = $profileType.GetMethods($flags) | Where-Object { $_.Name -eq "Random" -and $_.GetParameters().Count -eq 2 }
$makeGame = $profileType.GetMethod("ForGameStrike", $flags)
$varied = $flickerType.GetMethods($flags) | Where-Object { $_.Name -eq "Intensity" -and $_.GetParameters().Count -eq 4 }

function Field($profile, [string]$name) { return $profileType.GetField($name).GetValue($profile) }

$durations = @(); $strokeCounts = @(); $powers = @(); $reseeds = @(); $allInRange = $true; $allEnd = $true; $allPeak = $true; $allFadeOut = $true
foreach ($seed in 1..40) {
    [object[]]$a = (New-Object System.Random $seed).psobject.BaseObject, (($seed % 2) -eq 0)
    $profile = $makeRandom.Invoke($null, $a)
    $frames = [uint32](Field $profile "Frames"); $strokes = [int](Field $profile "Strokes")
    $durations += $frames; $strokeCounts += $strokes; $powers += [single](Field $profile "Power"); $reseeds += [uint32](Field $profile "Reseed")

    $s = [uint32](7000 + 97 * $seed)
    $values = 0..($frames + 3) | ForEach-Object { [object[]]$b = [uint32]($s + $_), $s, [single]0.0, $profile; [single]$varied.Invoke($null, $b) }
    $smooth = 0..($frames + 3) | ForEach-Object { [object[]]$b = [uint32]($s + $_), $s, [single]1.0, $profile; [single]$varied.Invoke($null, $b) }

    if ((($values | Measure-Object -Minimum).Minimum -lt 0) -or (($values | Measure-Object -Maximum).Maximum -gt 1)) { $allInRange = $false }
    if ((($values[$frames..($frames + 3)] | Measure-Object -Maximum).Maximum) -ne 0) { $allEnd = $false }
    if ((($smooth | Measure-Object -Maximum).Maximum) -lt 0.8) { $allPeak = $false }
    if ($smooth[$frames - 1] -gt 0.12) { $allFadeOut = $false }

    if ($seed -le 6) {
        $peaks = 0; for ($i = 1; $i -lt $smooth.Count - 1; $i++) { if (($smooth[$i] -gt $smooth[$i - 1]) -and ($smooth[$i] -ge $smooth[$i + 1]) -and ($smooth[$i] -gt 0.05)) { $peaks++ } }
        "  seed $seed : $(($frames / 60).ToString('F2')) s, $strokes stroke(s) -> $peaks peak(s) in the envelope, flicker every $(Field $profile 'Reseed') frames, power $(([single](Field $profile 'Power')).ToString('F2')), reach $(([single](Field $profile 'Reach')).ToString('F0')) m"
    }
}
"  over 40 seeds: duration $((($durations | Measure-Object -Minimum).Minimum / 60).ToString('F2'))..$((($durations | Measure-Object -Maximum).Maximum / 60).ToString('F2')) s; strokes $(($strokeCounts | Measure-Object -Minimum).Minimum)..$(($strokeCounts | Measure-Object -Maximum).Maximum); power $((($powers | Measure-Object -Minimum).Minimum).ToString('F2'))..$((($powers | Measure-Object -Maximum).Maximum).ToString('F2')); reseed $(($reseeds | Measure-Object -Minimum).Minimum)..$(($reseeds | Measure-Object -Maximum).Maximum)"
Check "durations actually vary, and stay within 0.23..1.6 s" (((($durations | Measure-Object -Minimum).Minimum) -ge 14) -and ((($durations | Measure-Object -Maximum).Maximum) -le 96) -and ((($durations | Sort-Object -Unique).Count) -ge 12))
Check "every intensity is within 0..1" $allInRange
Check "every strike is exactly 0 from its last frame on" $allEnd
Check "every strike's envelope reaches at least 0.8" $allPeak
Check "every strike has faded out by its last frame (no cut-off)" $allFadeOut

[object[]]$g = ,(New-Object System.Random 5).psobject.BaseObject
$gameProfile = $makeGame.Invoke($null, $g)
$sameAsGame = $true
foreach ($age in 0..29) { [object[]]$b = [uint32]($start + $age), $start, [single]0.3, $gameProfile; if ([single]$varied.Invoke($null, $b) -ne (Level $age 0.3)) { $sameAsGame = $false } }
Check "a GAME strike's profile still gives the game's formula, frame for frame (30 frames, exact)" ($sameAsGame -and ([uint32](Field $gameProfile "Frames") -eq 30) -and [bool](Field $gameProfile "Exact"))

""
"=== LightningBoltMesh.Channel: main stroke from the cloud (750 m) to the ground (60 m) ==="
$channel = $asm.GetType("VolumetricClouds.Sky.LightningBoltMesh").GetMethod("Channel", $flags)
$top = New-Object UnityEngine.Vector3 100, 750, -200
$ground = New-Object UnityEngine.Vector3 160, 60, -150
$length = ($ground - $top).magnitude
$axis = ($ground - $top).normalized

foreach ($seed in 1, 2, 3, 4) {
    [object[]]$a = $top.psobject.BaseObject, $ground.psobject.BaseObject, 6, [single]0.13, (New-Object System.Random $seed).psobject.BaseObject
    $pts = $channel.Invoke($null, $a)

    $path = 0.0; $sideways = 0.0; $up = 0; $shortest = 1e9; $longest = 0.0
    for ($i = 0; $i -lt $pts.Count; $i++) {
        $rel = $pts[$i] - $top
        $dev = ($rel - $axis * [UnityEngine.Vector3]::Dot($rel, $axis)).magnitude
        if ($dev -gt $sideways) { $sideways = $dev }
        if ($i -gt 0) {
            $seg = ($pts[$i] - $pts[$i - 1]).magnitude
            $path += $seg
            if ($seg -lt $shortest) { $shortest = $seg }
            if ($seg -gt $longest) { $longest = $seg }
            if ($pts[$i].y -gt $pts[$i - 1].y) { $up++ }
        }
    }

    "  seed $seed : $($pts.Count) points, straight $($length.ToString('F0')) m, path $($path.ToString('F0')) m (x$(($path / $length).ToString('F2'))), max sideways $($sideways.ToString('F0')) m, segments $($shortest.ToString('F1'))..$($longest.ToString('F1')) m, $up of 64 go upwards"
    Check "seed $seed has 2^6 + 1 points and exact ends" (($pts.Count -eq 65) -and (($pts[0] - $top).magnitude -lt 1e-3) -and (($pts[64] - $ground).magnitude -lt 1e-3))
    Check "seed $seed wanders, but like a bolt rather than a scribble (path 1.05x..1.8x, sideways under 35% of its length)" ((($path / $length) -gt 1.05) -and (($path / $length) -lt 1.8) -and ($sideways -lt 0.35 * $length))
    Check "seed $seed mostly heads down (under a quarter of its segments rise)" ($up -lt 16)
}

""
"=== LightningBoltShape (Lightning variety, 2026-11-15) ==="
$shapeType = $asm.GetType("VolumetricClouds.Sky.LightningBoltShape")
$kindType = $asm.GetType("VolumetricClouds.Sky.LightningBoltShape+Kind")
$ribbonType = $asm.GetType("VolumetricClouds.Sky.LightningBoltShape+Ribbon")
$build = $shapeType.GetMethod("Build", $flags)
$reaches = $shapeType.GetMethod("ReachesGround", $flags)
$listType = [System.Collections.Generic.List``1].MakeGenericType($ribbonType)
$top = New-Object UnityEngine.Vector3 0, 1400, 0
$ground = New-Object UnityEngine.Vector3 300, 40, -200
$base = [single]1200
foreach ($k in [Enum]::GetValues($kindType)) {
    $allOk = $true; $landOk = $true; $pathOk = $true; $ribbons = 0
    foreach ($seed in 1..40) {
        $list = [Activator]::CreateInstance($listType)
        [object[]]$a = $k, $top.psobject.BaseObject, $ground.psobject.BaseObject, $base, (New-Object System.Random $seed).psobject.BaseObject, $list
        [void]$build.Invoke($null, $a)
        $ribbons += $list.Count
        if ($list.Count -lt 1) { $allOk = $false; continue }
        $landed = $false
        foreach ($r in $list) {
            if ($r.PathStart -lt 0 -or $r.PathEnd -gt 1.0001 -or $r.PathEnd -lt $r.PathStart) { $pathOk = $false }
            foreach ($p in $r.Points) {
                if ([single]::IsNaN($p.x) -or [single]::IsNaN($p.y) -or [single]::IsNaN($p.z)) { $allOk = $false }
            }
            $last = $r.Points[$r.Points.Count - 1]
            if (($last - $ground).magnitude -lt 1) { $landed = $true }
        }
        [object[]]$ka = @($k)
        $ground_kind = [bool]$reaches.Invoke($null, $ka)
        if ($ground_kind -and -not $landed) { $landOk = $false }
    }
    "  $k : $([math]::Round($ribbons / 40.0, 1)) ribbons a bolt"
    Check "$k : 40 seeds, every ribbon has points and no NaN" $allOk
    Check "$k : path positions inside 0..1 and increasing along a ribbon" $pathOk
    Check "$k : a ground kind lands exactly on the ground point (a game strike's fire is there)" $landOk
}

$pick = $shapeType.GetMethod("Pick", $flags)
$counts = @{}
$rnd = New-Object System.Random 7
for ($i = 0; $i -lt 20000; $i++) {
    [object[]]$a = $rnd.NextDouble(), [single]1, $true, $false, $false
    $kk = [int]$pick.Invoke($null, $a)
    $counts[$kk] = 1 + $(if ($counts.ContainsKey($kk)) { $counts[$kk] } else { 0 })
}
"  ground kinds at 100%: " + (($counts.Keys | Sort-Object | ForEach-Object { "$([Enum]::ToObject($kindType, $_)) $([math]::Round(100.0 * $counts[$_] / 20000))%" }) -join ", ")
[object[]]$a0 = [double]0.9, [single]0, $true, $false, $false
Check "variety 0 = always Classic (the 1.5.3 bolt)" ([int]$pick.Invoke($null, $a0) -eq 0)
[object[]]$ag = [double]0.999, [single]2, $true, $true, $false
Check "a game strike never dies in the air (its fire lands where the game says)" ([int]$pick.Invoke($null, $ag) -ne 4)

"=== LightningBursts ==="
$bursts = $asm.GetType("VolumetricClouds.Sky.LightningBursts")
$expected = $bursts.GetMethod("ExpectedSize", $flags)
$drawB = $bursts.GetMethod("Draw", $flags)
$followType = $asm.GetType("VolumetricClouds.Sky.LightningBursts+FollowUp")
$fl = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($followType))
foreach ($case in @(@(0.5, 1.0), @(3.1, 1.0), @(4.0, 2.0), @(2.0, 0.0))) {
    $act = [single]$case[0]; $v = [single]$case[1]
    $rnd = New-Object System.Random 11; $total = 0
    for ($i = 0; $i -lt 100000; $i++) {
        [object[]]$a = $rnd.psobject.BaseObject, $act, $v, $fl
        [void]$drawB.Invoke($null, $a)
        $total += 1 + $fl.Count
    }
    [object[]]$ea = $act, $v
    $e = [single]$expected.Invoke($null, $ea)
    $mean = $total / 100000.0
    "  activity $act, variety $v : $([math]::Round($mean, 3)) strikes per first strike, expected $([math]::Round($e, 3))"
    Check "activity $act variety $v : the mean burst size is what the rate divides by (within 3%)" ([math]::Abs($mean - $e) / $e -lt 0.03)
}
$tint = $bursts.GetMethod("DistanceTint", $flags)
[object[]]$t7 = [single]7000, [single]1
$c7 = $tint.Invoke($null, $t7)
[object[]]$t0 = [single]7000, [single]0
$c0 = $tint.Invoke($null, $t0)
"  7 km tint: ($($c7.r.ToString('F2')), $($c7.g.ToString('F2')), $($c7.b.ToString('F2')))"
Check "far strikes dimmer and pinker (red > blue > green), variety 0 = untouched" (($c7.r -gt $c7.b) -and ($c7.b -gt $c7.g) -and ($c7.r -lt 1) -and ($c0.r -eq 1) -and ($c0.g -eq 1))

"=== Leader ==="
$flickerType = $asm.GetType("VolumetricClouds.Sky.LightningFlicker")
$profileType = $asm.GetType("VolumetricClouds.Sky.LightningFlicker+Profile")
$randomV = $profileType.GetMethods($flags) | Where-Object { $_.Name -eq "Random" -and $_.GetParameters().Count -eq 4 }
$random2 = $profileType.GetMethods($flags) | Where-Object { $_.Name -eq "Random" -and $_.GetParameters().Count -eq 2 }
$reveal = $flickerType.GetMethod("LeaderReveal", $flags)
$int4 = $flickerType.GetMethods($flags) | Where-Object { $_.Name -eq "Intensity" -and $_.GetParameters().Count -eq 4 }
$leaders = 0; $monotone = $true; $peakAfter = $true
foreach ($seed in 1..200) {
    [object[]]$a = (New-Object System.Random $seed).psobject.BaseObject, $true, 0, [single]1
    $p = $randomV.Invoke($null, $a)
    if ($p.LeaderFrames -eq 0) { continue }
    $leaders++
    $prev = -1.0
    for ($f = 0; $f -lt $p.LeaderFrames; $f++) {
        [object[]]$ra = [uint32](9000 + $f), [uint32]9000, $p
        $r = [single]$reveal.Invoke($null, $ra)
        if ($r -lt $prev - 0.05 -or $r -lt 0 -or $r -gt 1) { $monotone = $false }
        $prev = $r
    }
    [object[]]$after = [uint32](9000 + $p.LeaderFrames), [uint32]9000, $p
    if ([single]$reveal.Invoke($null, $after) -ge 0) { $monotone = $false }
    $lead = 0.0; $main = 0.0
    for ($f = 0; $f -lt $p.Frames; $f++) {
        [object[]]$ia = [uint32](9000 + $f), [uint32]9000, [single]0, $p
        $x = [single]$int4.Invoke($null, $ia)
        if ($f -lt $p.LeaderFrames) { $lead = [math]::Max($lead, $x) } else { $main = [math]::Max($main, $x) }
    }
    if ($lead -gt 0.15 -or $main -lt 0.3) { $peakAfter = $false }
}
"  $leaders of 200 channels to the ground have a leader at 100%"
Check "the leader crawls down (0..1, rising, gone when the stroke flashes)" $monotone
Check "the leader is faint (<= 0.15) and the stroke flashes after it" $peakAfter
[object[]]$a0 = (New-Object System.Random 5).psobject.BaseObject, $true, 0, [single]0
[object[]]$a1 = (New-Object System.Random 5).psobject.BaseObject, $true
$pv0 = $randomV.Invoke($null, $a0); $pold = $random2.Invoke($null, $a1)
Check "variety 0 = the 1.5.3 profile exactly (no leader, same frames, tint)" (($pv0.LeaderFrames -eq 0) -and ($pv0.Frames -eq $pold.Frames) -and ($pv0.Tint -eq $pold.Tint) -and ($pv0.Power -eq $pold.Power))

"=== StormCellRules (Travelling storms) ==="
$rules = $asm.GetType("VolumetricClouds.Sky.StormCellRules")
$env = $rules.GetMethod("Envelope", $flags)
$birth = $rules.GetMethod("BirthDistance", $flags)
$steer = $rules.GetMethod("Steer", $flags)
$wedge = $rules.GetMethod("InWedge", $flags)
foreach ($speed in 0.5, 6, 15, 40) {
    [object[]]$ba = @([single]$speed)
    $d = [single]$birth.Invoke($null, $ba)
    $ground = [math]::Max($speed, 6)
    $eta = $d / $ground / 60
    "  clouds at $speed m/s: born $([math]::Round($d / 1000, 1)) km upwind, arrives in $([math]::Round($eta, 1)) min"
    Check "speed $speed : born 2.2-9 km upwind, arriving in 1.5-6.5 min" (($d -ge 2200) -and ($d -le 9000) -and ($eta -ge 1.5) -and ($eta -le 6.5))
}
# A cell born 6 km upwind (wind +x), 2 km to the side, steered: where does its track pass the look-at point?
[object[]]$sa = [single]1, [single]0, [single]3, [single]2000, [single]6000
$s = $steer.Invoke($null, $sa)
$t = 6000 / [math]::Max(3, 6)
$sideAtArrival = 2000 + (- $s.y) * $t   # right of +x wind = -z; side + across*t
"  steer ($([math]::Round($s.x, 2)), $([math]::Round($s.y, 2))) m/s; side when it arrives: $([math]::Round($sideAtArrival)) m"
Check "the steered track passes within 300 m of the look-at point" ([math]::Abs($sideAtArrival) -lt 300)
$e = @(-9000, -4000, 0, 3000, 7000, 9500) | ForEach-Object { [object[]]$ea = [single]$_, [single]300; [single]$env.Invoke($null, $ea) }
"  envelope along -9, -4, 0, +3, +7, +9.5 km: " + (($e | ForEach-Object { $_.ToString("F2") }) -join " ")
Check "strongest overhead, weaker far off, gone past 9 km downwind" (($e[2] -gt $e[1]) -and ($e[1] -gt $e[0]) -and ($e[2] -gt $e[3]) -and ($e[3] -gt $e[4]) -and ($e[5] -eq 0) -and ($e[0] -gt 0))
[object[]]$young = [single]0, [single]5
Check "a new cell grows in over its first 40 s" ([single]$env.Invoke($null, $young) -lt 0.2)
[object[]]$w1 = [single]0, [single]0, [single]0, [single]1, [single]0.45, [single]300, [single]3000
[object[]]$w2 = [single]0, [single]0, [single]0, [single]1, [single]0.45, [single]3000, [single]300
Check "the view wedge: ahead in, to the side out" ([bool]$wedge.Invoke($null, $w1) -and -not [bool]$wedge.Invoke($null, $w2))

""
if ($failed -eq 0) { "ALL CHECKS PASSED" } else { "$failed CHECK(S) FAILED"; exit 1 }
