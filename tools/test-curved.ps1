# Offline checks of Sky/CurvedClouds.cs -- the Curved clouds trial (2026-10-23): the layer over the
# curved Earth, where a ray is in it, how far the march goes, the haze. The class has no Unity
# dependency, so the SOURCE is compiled directly (as test-emptysky does).
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-curved.ps1
#
# What it guards: the numbers the plan rests on (the drop, where the base meets the horizon), the
# stable roots, and the shader's way of finding a ray's stretch in the layer (CurvedClouds.Slab, the
# C# twin of CloudRaymarch CurvedSlab) against a brute-force walk along the ray, from under the base,
# inside the layer and over it, near the map's centre and at its corner. Then the far sky (2026-10-30):
# the patches' weights (whole, continuous, mostly one read), the lattice riding with the drift, every
# patch's turn keeping a whole repeat whole, the blend keeping the map's contrast, the fronts' range.
# Drift.cs is compiled beside it (the lattice's phase).

Add-Type -Path ("CurvedClouds.cs", "Drift.cs" | ForEach-Object { Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\$_" })

$C = [VolumetricClouds.Sky.CurvedClouds]
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

function Near([double]$a, [double]$b, [double]$tol) { return [math]::Abs($a - $b) -le $tol }

# ---- the strength --------------------------------------------------------------------------------

Check "off below 0" ($C::Strength(-1) -eq 0)
Check "off at 0" ($C::Strength(0) -eq 0)
Check "NaN is off" ($C::Strength([float]::NaN) -eq 0)
Check "100% is the real Earth" ($C::Strength(1) -eq 1)
Check "clamped at 400%" ($C::Strength(9) -eq 4)
Check "no curve when off" ($C::InverseTwoRadius(0) -eq 0 -and $C::Drop(0, 30000, 0) -eq 0)

# ---- the plan's numbers (base 850 m) -------------------------------------------------------------

$d17 = $C::Drop(1, 17000, 0); $d30 = $C::Drop(1, 30000, 0); $d100 = $C::Drop(1, 0, 100000)
Check "drop at 17 km ~23 m" (Near $d17 22.7 0.5) ("{0:F1} m" -f $d17)
Check "drop at 30 km ~71 m" (Near $d30 70.6 0.5) ("{0:F1} m" -f $d30)
Check "drop at 100 km ~785 m" (Near $d100 784.8 1) ("{0:F1} m" -f $d100)
Check "drop is x strength" (Near $C::Drop(4, 17000, 0) (4 * $d17) 0.01)
$h1 = $C::HorizonDistance(1, 850); $h4 = $C::HorizonDistance(4, 850)
Check "850 m base meets the horizon ~104 km out" (Near $h1 104070 300) ("{0:F0} m" -f $h1)
Check "at 400% half as far" (Near $h4 ($h1 / 2) 1) ("{0:F0} m" -f $h4)

# ---- the far sky's patches never reach the C# twins (they only look on the map: corner 12.2 km + rain slant)

Check "patches begin past the map's corner" ($C::PatchFrom -ge 12220 + 3000) ("{0} m" -f $C::PatchFrom)
Check "patches ease in" ($C::PatchBlend -gt 0 -and $C::FrontBlend -gt 0)

# ---- the patches (twins of CloudCommon FarLookAt / PatchUV / FarBlend) ---------------------------

$tile = 54000.0; $spacing = $C::PatchSpacing * $tile; $inv = [float](1.0 / $spacing)
$cells = New-Object float[] 6; $w = New-Object float[] 3
$r1 = New-Object System.Random 4242
function Rand([double]$span) { return [float](($r1.NextDouble() * 2 - 1) * $span) }

# Weights: never negative, summing to 1; most of the sky reads one patch alone.
$bad = 0; $single = 0; $n = 4000
for ($i = 0; $i -lt $n; $i++) {
    $x = Rand 250000; $z = Rand 250000
    $C::Patches($x, $z, 3.7, 11.2, $inv, $cells, $w)
    $sum = $w[0] + $w[1] + $w[2]
    if ([math]::Abs($sum - 1) -gt 1e-5 -or $w[0] -lt 0 -or $w[1] -lt 0 -or $w[2] -lt 0) { $bad++ }
    if ((@($w | Where-Object { $_ -gt 0 })).Count -eq 1) { $single++ }
}
Check "weights sum to 1, never negative" ($bad -eq 0) ("{0} bad of {1}" -f $bad, $n)
Check "most of the sky reads one patch" ($single -gt $n * 0.8) ("{0:P0} single" -f ($single / $n))

# Continuity: a patch's weight changes smoothly -- the weight a given cell gets at two points 1 m apart.
function WeightOf([float]$x, [float]$z, [float]$cx, [float]$cz) {
    $C::Patches($x, $z, 3.7, 11.2, $inv, $cells, $w)
    for ($k = 0; $k -lt 3; $k++) { if ($cells[2 * $k] -eq $cx -and $cells[2 * $k + 1] -eq $cz) { return $w[$k] } }
    return 0.0
}
$jump = 0.0
for ($i = 0; $i -lt 3000; $i++) {
    $x = Rand 250000
    $z = Rand 250000
    $C::Patches($x, $z, 3.7, 11.2, $inv, $cells, $w)
    for ($k = 0; $k -lt 3; $k++) {
        $cx = $cells[2 * $k]; $cz = $cells[2 * $k + 1]
        $a = WeightOf $x $z $cx $cz; $b = WeightOf ($x + 1) ($z + 1) $cx $cz
        $jump = [math]::Max($jump, [math]::Abs($a - $b))
    }
}
Check "patch weights continuous (1 m apart)" ($jump -lt 0.01) ("largest change {0:E1}" -f $jump)

# The lattice rides with the wind: after a drift D the patches at p + D are the ones that were at p.
$d = 1234567.89; $dz = -765432.1
$px0 = [float]0; $pz0 = [float]0; $px1 = [float]0; $pz1 = [float]0
$C::LatticePhase(0, 0, $spacing, [ref]$px0, [ref]$pz0)
$C::LatticePhase($d, $dz, $spacing, [ref]$px1, [ref]$pz1)
$moved = 0
for ($i = 0; $i -lt 200; $i++) {
    $x = Rand 50000; $z = Rand 50000
    $c0 = New-Object float[] 6; $c1 = New-Object float[] 6; $w0 = New-Object float[] 3; $w1 = New-Object float[] 3
    $C::Patches($x, $z, $px0, $pz0, $inv, $c0, $w0)
    # The same point carried by the drift (its cells differ by whole lattice repeats: compare hashes).
    $xr = [float]($x + $d); $zr = [float]($z + $dz)
    $C::Patches($xr, $zr, $px1, $pz1, $inv, $c1, $w1)
    $h0 = 0.0; $h1 = 0.0; $hh = [float]0; $hz = [float]0; $hw = [float]0
    $C::PatchHash($c0[0], $c0[1], [ref]$hh, [ref]$hz, [ref]$hw); $h0 = $hh + $hz
    $C::PatchHash($c1[0], $c1[1], [ref]$hh, [ref]$hz, [ref]$hw); $h1 = $hh + $hz
    if ((Near $w0[0] $w1[0] 0.02) -and (Near $h0 $h1 1e-4)) { $moved++ }
}
Check "the lattice rides with the drift" ($moved -ge 196) ("{0} of 200 points" -f $moved)

# Every patch's turn keeps the map's repeat whole (the drift wraps by whole repeats: no jump).
$whole = 0
for ($i = 0; $i -lt 500; $i++) {
    $cx = [float][math]::Floor((Rand 200)); $cz = [float][math]::Floor((Rand 200))
    $u = Rand 10; $v = Rand 10
    $a1 = [float]0; $b1 = [float]0; $a2 = [float]0; $b2 = [float]0; $a3 = [float]0; $b3 = [float]0
    $C::PatchUV($u, $v, $cx, $cz, [ref]$a1, [ref]$b1)
    $C::PatchUV($u + 1, $v, $cx, $cz, [ref]$a2, [ref]$b2)
    $C::PatchUV($u, $v + 1, $cx, $cz, [ref]$a3, [ref]$b3)
    $ok = $true
    foreach ($q in ($a2 - $a1), ($b2 - $b1), ($a3 - $a1), ($b3 - $b1)) { if (-not (Near $q ([math]::Round($q)) 1e-4)) { $ok = $false } }
    if ($ok) { $whole++ }
}
Check "a whole repeat stays whole in every patch" ($whole -eq 500) ("{0} of 500" -f $whole)

# The patches differ: all eight turns, offsets spread over the map.
$turns = @{}; $offs = New-Object int[] 4
for ($cx = 0; $cx -lt 64; $cx++) { for ($cz = 0; $cz -lt 64; $cz++) {
    $a = [float]0; $b = [float]0
    $C::PatchUV(0.3, 0.1, $cx, $cz, [ref]$a, [ref]$b)
    $hh = [float]0; $h1 = [float]0; $h2 = [float]0
    $C::PatchHash($cx, $cz, [ref]$hh, [ref]$h1, [ref]$h2)
    $turns[[int][math]::Floor($hh * 8)] = 1
    $offs[[int][math]::Floor($h1 * 2) + 2 * [int][math]::Floor($h2 * 2)]++
} }
Check "all eight turns used" ($turns.Count -eq 8)
$minQ = ($offs | Measure-Object -Minimum).Minimum
Check "offsets spread over the map" ($minQ -gt 4096 / 4 * 0.8) ("quadrants {0}" -f ($offs -join ' '))

# The blend keeps the contrast: two halves of unrelated reads spread like one.
$r2 = New-Object System.Random 77
$acc = 0.0; $m = 0.5; $trials = 20000
foreach ($pair in @(@(0.5, 0.5), @(0.8, 0.2), @(0.34, 0.33, 0.33))) {
    $acc = 0.0
    for ($i = 0; $i -lt $trials; $i++) {
        $vals = New-Object float[] $pair.Count
        for ($k = 0; $k -lt $pair.Count; $k++) {
            $g = [math]::Sqrt(-2 * [math]::Log(1 - $r2.NextDouble())) * [math]::Cos(2 * [math]::PI * $r2.NextDouble())
            $vals[$k] = [float]($m + 0.1 * $g)
        }
        $b = $C::Blend([float[]]$pair, $vals, $m) - $m
        $acc += $b * $b
    }
    $sd = [math]::Sqrt($acc / $trials)
    Check ("blend keeps the spread ({0})" -f ($pair -join '/')) (Near $sd 0.1 0.004) ("{0:F4} (0.1000)" -f $sd)
}

# ---- the fronts ----------------------------------------------------------------------------------

$lo = 9.0; $hi = -9.0; $acc = 0.0; $steep = 0.0
for ($i = 0; $i -lt 5000; $i++) {
    $x = Rand 500000; $z = Rand 500000
    $f = $C::Front($x, $z)
    $lo = [math]::Min($lo, $f); $hi = [math]::Max($hi, $f); $acc += $f * $f
    $steep = [math]::Max($steep, [math]::Abs($C::Front($x + 1000, $z) - $f))
}
Check "fronts within -1.5..1.5" ($lo -ge -1.5 -and $hi -le 1.5) ("{0:F2} .. {1:F2}" -f $lo, $hi)
Check "fronts both ways" ($lo -lt -1 -and $hi -gt 1)
Check "fronts slow (per km)" ($steep -lt 0.15) ("{0:F3} at most per km" -f $steep)

# ---- the haze ------------------------------------------------------------------------------------

Check "presence 1 at the camera" (Near $C::Presence(0) 1 1e-6)
$p10 = $C::Presence(10000); $p30 = $C::Presence(30000); $p100 = $C::Presence(100000)
Check "presence falls" ($p10 -gt $p30 -and $p30 -gt $p100) ("10 km {0:F2}, 30 km {1:F2}, 100 km {2:F2}" -f $p10, $p30, $p100)
Check "presence never under 1 - Haze" ($C::Presence(1e9) -ge 1 - $C::Haze - 1e-6)

# ---- the reach -----------------------------------------------------------------------------------

$r = $C::Reach(1, 400, 1300, 30000)
Check "reach from 400 m ~200 km" (Near $r 200100 1500) ("{0:F0} m" -f $r)
Check "reach never under the flat one" ($C::Reach(4, 10, 300, 30000) -ge 30000)
Check "reach capped" ($C::Reach(1, 5000, 3000, 30000) -le $C::MaxReach)
Check "reach off = flat" ($C::Reach(0, 400, 1300, 30000) -eq 30000)

# ---- roots ---------------------------------------------------------------------------------------

$rng = New-Object System.Random 2310
$worst = 0.0
for ($i = 0; $i -lt 2000; $i++) {
    $qa = [math]::Pow(10, -9 + 3 * $rng.NextDouble())
    $qb = ($rng.NextDouble() * 2 - 1) * [math]::Pow(10, -4 + 4 * $rng.NextDouble())
    $qc = ($rng.NextDouble() * 2 - 1) * 3000
    $t1 = 0.0; $t2 = 0.0
    if ($C::Roots($qa, $qb, $qc, [ref]$t1, [ref]$t2)) {
        foreach ($t in $t1, $t2) {
            # Relative to the size of the terms at that t.
            $scale = [math]::Max([math]::Abs($qa * $t * $t) + [math]::Abs($qb * $t) + [math]::Abs($qc), 1e-9)
            $err = [math]::Abs($qa * $t * $t + $qb * $t + $qc) / $scale
            if ($err -gt $worst) { $worst = $err }
        }
        if ($t1 -gt $t2) { $worst = 1 }
    }
}
Check "roots solve it, smaller first" ($worst -lt 1e-9) ("worst relative residual {0:E1}" -f $worst)

# ---- the stretch in the layer, against a walk along the ray -------------------------------------

function StateAt([double]$k, [double[]]$o, [double[]]$d, [double]$bottom, [double]$top, [double]$t) {
    # Where the ray is at t: its height over the curved Earth (centred on the map's centre).
    $x = $o[0] + $d[0] * $t; $z = $o[2] + $d[2] * $t
    $h = $o[1] + $d[1] * $t + $k * ($x * $x + $z * $z)
    if ($h -le 0) { return 'ground' }
    if ($h -lt $bottom) { return 'under' }
    if ($h -lt $top) { return 'in' }
    return 'over'
}

function Walk([double]$k, [double[]]$o, [double[]]$d, [double]$bottom, [double]$top, [double]$tMax) {
    # The first t >= 0 in the layer; then the first t it is out again (or under the ground). Steps of
    # 50 m, longer further out (a grazing stretch in the layer is kilometres long), then the edges found
    # by halving.
    $prev = StateAt $k $o $d $bottom $top 0.0
    $enter = $null
    $t = 0.0
    if ($prev -eq 'in') { $enter = 0.0 }
    while ($t -lt $tMax) {
        $next = $t + 50.0 + 0.002 * $t
        $s = StateAt $k $o $d $bottom $top $next
        if ($s -ne $prev) {
            $lo = $t; $hi = $next
            for ($j = 0; $j -lt 40; $j++) { $mid = ($lo + $hi) / 2; if ((StateAt $k $o $d $bottom $top $mid) -eq $prev) { $lo = $mid } else { $hi = $mid } }
            if ($null -eq $enter) {
                if ($s -eq 'ground') { return $null }
                if ($s -eq 'in') { $enter = $hi }
            } elseif ($s -ne 'in') {
                return @($enter, $hi)
            }
            $prev = $s
        }
        $t = $next
        if ($null -eq $enter -and $prev -eq 'ground') { return $null }
    }
    if ($null -ne $enter) { return @($enter, $tMax) }
    return $null
}

$bottom = 850.0; $top = 1300.0
$cases = 0; $agree = 0; $crossing = 0; $bad = @()
foreach ($strength in 1.0, 4.0) {
    $k = $C::InverseTwoRadius($strength)
    foreach ($eye in @(@(0, 300, 0), @(8000, 600, -5000), @(0, 1000, 0), @(-8600, 1600, 8600), @(3000, 4000, 0))) {
        for ($i = 0; $i -lt 14; $i++) {
            # Rays round the horizon, where the curve matters: -3 to +6 degrees, any bearing.
            $el = (-3 + 9 * $rng.NextDouble()) * [math]::PI / 180
            $az = $rng.NextDouble() * 2 * [math]::PI
            # (Each in brackets: PowerShell's comma binds tighter than *.)
            $d = @(([math]::Cos($el) * [math]::Cos($az)), ([math]::Sin($el)), ([math]::Cos($el) * [math]::Sin($az)))
            $enter = [float]0; $exit = [float]0
            $hit = $C::Slab([float]$k, [float]$eye[0], [float]$eye[1], [float]$eye[2], [float]$d[0], [float]$d[1], [float]$d[2],
                            [float]$bottom, [float]$top, [ref]$enter, [ref]$exit)
            $walk = Walk $k $eye $d $bottom $top 300000
            $cases++
            if (-not $hit -and $null -eq $walk) { $agree++; continue }
            if ($hit -and $null -ne $walk -and (Near $enter $walk[0] 5) -and ((Near $exit $walk[1] 5) -or ($exit -gt 300000 -and $walk[1] -ge 299999))) { $agree++; $crossing++; continue }
            $bad += ("strength {0} eye ({1}) el {2:F2} deg: slab {3} [{4:F0}, {5:F0}] walk {6}" -f $strength, ($eye -join ','), ($el * 180 / [math]::PI), $hit, $enter, $exit, ($(if ($walk) { "[{0:F0}, {1:F0}]" -f $walk[0], $walk[1] } else { "none" })))
        }
    }
}
Check "slab = the walk along the ray" ($agree -eq $cases -and $crossing -gt $cases / 3) ("{0} of {1} rays, {2} of them in the layer" -f $agree, $cases, $crossing)
foreach ($line in ($bad | Select-Object -First 6)) { Write-Host "      $line" }

if ($failed -gt 0) { Write-Host "$failed FAILED"; exit 1 }
Write-Host "all passed"
