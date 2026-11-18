# Offline checks of Sky/ClassicShape.cs -- "Natural Classic clouds" (2026-11-17), the Classic layer's shape; the shader's
# LayerDensity is its twin. The class has no Unity dependency, so the SOURCE is compiled directly (as test-soft does), no
# game and no built DLL needed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-classicshape.ps1
#
# What it guards: the row at 0 is the old slab number for number (nobody's clouds change); at any row the shape is never
# over the old slab at any height (no cloud grows taller or denser, no core pushed up, the base stays), never where the
# old shape had no cloud (the footprint, the empty-sky skip and the rain stay inside), and at the widest level where the
# map is over its cut the cloud is still there (the footprint from below holds); a cloud thickens towards its middle (a
# dome) and its underside comes down as it does; every row lies between the old shape and the new one.

$source = Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\ClassicShape.cs"
Add-Type -TypeDefinition (Get-Content $source -Raw)

$C = [VolumetricClouds.Sky.ClassicShape]
$soft = 0.18
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

function Old([double]$e, [double]$h) {
    $cov = [math]::Max(0.0, [math]::Min(1.0, $e / $soft))
    return [float]($cov * $C::HeightShape([float]$h))
}

$excesses = @(); for ($i = -20; $i -le 120; $i++) { $excesses += $i * 0.01 }
$heights = @(); for ($i = 0; $i -le 100; $i++) { $heights += $i * 0.01 }

# ---- the row at 0: the old slab, number for number ---------------------------------------------------
$same = $true; $worst = ""
foreach ($e in $excesses) { foreach ($h in $heights) {
    $a = $C::Shaped([float]$e, [float]$soft, [float]$h, [float]0)
    $b = [float]([math]::Max(0.0, [math]::Min(1.0, [float]$e / [float]$soft)) * $C::HeightShape([float]$h))
    # (PowerShell multiplies the two floats in doubles: one float rounding apart at most.)
    if ([math]::Abs($a - $b) -gt 1e-6) { $same = $false; $worst = "e $e h $h : $a vs $b" }
} }
Check "row 0 = the old slab (141 x 101 points, within one float rounding)" $same $worst

# ---- never over the old slab, never outside its footprint --------------------------------------------
$under = $true; $inside = $true; $w1 = ""; $w2 = ""
foreach ($row in 0.25, 0.5, 0.75, 1.0) { foreach ($e in $excesses) { foreach ($h in $heights) {
    $s = $C::Shaped([float]$e, [float]$soft, [float]$h, [float]$row)
    if ($s -gt $C::HeightShape([float]$h) + 1e-6) { $under = $false; $w1 = "row $row e $e h $h : $s" }
    if ($e -le 0 -and $s -ne 0) { $inside = $false; $w2 = "row $row e $e h $h : $s" }
} } }
Check "never denser than the old slab at any height (never taller, the base stays)" $under $w1
Check "nothing where the map is at or under its cut (footprint, skip and rain inside)" $inside $w2

# ---- the footprint at the widest level ----------------------------------------------------------------
$mid = $C::Middle
$there = $true; $w = ""
foreach ($e in $excesses) {
    if ($e -le 0) { continue }
    $n = $C::Natural([float]$e, [float]$soft, [float]$mid)
    $o = Old $e $mid
    if (-not ($n -gt 0) -or $n -lt $o - 1e-6) { $there = $false; $w = "e $e : new $n old $o" }
}
Check "at the widest level as dense as the old shape wherever the map is over its cut" $there $w

# ---- the dome: thickness grows towards the middle, the underside comes down ---------------------------
function Thickness([double]$e, [double]$row) {
    $n = 0; $low = 1.0; $top = 0.0
    foreach ($h in $heights) { if ($C::Shaped([float]$e, [float]$soft, [float]$h, [float]$row) -gt 0.02) { $n++; if ($h -lt $low) { $low = $h }; if ($h -gt $top) { $top = $h } } }
    return @($n, $low, $top)
}
$grows = $true; $comesDown = $true; $prev = @(0, 1.0, 0.0); $w = ""
foreach ($e in $excesses) {
    if ($e -le 0) { continue }
    $t = Thickness $e 1.0
    if ($t[0] -lt $prev[0]) { $grows = $false; $w = "thinner at e $e ($($t[0]) < $($prev[0]))" }
    if ($t[0] -gt 0 -and $prev[0] -gt 0 -and $t[1] -gt $prev[1] + 1e-9) { $comesDown = $false; $w = "base rises at e $e" }
    $prev = $t
}
Check "row 100: a cloud thickens towards its middle" $grows $w
Check "row 100: its underside comes down towards its middle" $comesDown $w
$rim = Thickness 0.05 1.0; $rimOld = Thickness 0.05 0.0
$core = Thickness 1.0 1.0; $coreOld = Thickness 1.0 0.0
Check "row 100: near the edge (0.05 over the cut) thinner than the old slab" ($rim[0] -lt $rimOld[0]) "($($rim[0]) vs $($rimOld[0]) of 101 levels; base $($rim[1]) vs $($rimOld[1]), top $($rim[2]) vs $($rimOld[2]))"
Check "row 100: deep inside (1.0 over the cut) the old slab's thickness" ($core[0] -eq $coreOld[0]) "($($core[0]) vs $($coreOld[0]))"

# ---- each height grows with the map; a row between lies between ----------------------------------------
$mono = $true; $between = $true; $w1 = ""; $w2 = ""
foreach ($h in $heights) {
    $p = -1.0
    foreach ($e in $excesses) {
        $n = $C::Natural([float]$e, [float]$soft, [float]$h)
        if ($n -lt $p - 1e-7) { $mono = $false; $w1 = "h $h e $e" }
        $p = $n
        $o = Old $e $h
        $half = $C::Shaped([float]$e, [float]$soft, [float]$h, [float]0.5)
        if ($half -lt [math]::Min($o, $n) - 1e-6 -or $half -gt [math]::Max($o, $n) + 1e-6) { $between = $false; $w2 = "h $h e $e" }
    }
}
Check "the new shape never shrinks as the map rises (at every height)" $mono $w1
Check "the row at 50% lies between the old shape and the new" $between $w2

# ---- the soft rim -----------------------------------------------------------------------------------------
Check "rim: none with the row at 0, at any cover" ($C::RimAt(0, 0.3) -eq 0 -and $C::RimAt(0, 0.9) -eq 0)
Check "rim: the edge exactly 1x with no rim" ($C::EdgeWiden(0.05, $soft, 0) -eq 1 -and $C::EdgeWiden(-0.1, $soft, 0) -eq 1)
$r0 = $C::RimAt(1, 0.5); $rD = $C::RimAt(1, $C::DeckTo); $rM = $C::RimAt(1, 0.9)
Check "rim: full under the deck's start, none over a closed deck, between in between" ([math]::Abs($r0 - $C::RimSoftness) -lt 1e-6 -and $rD -eq 0 -and $rM -gt 0 -and $rM -lt $r0) "($r0, $rM, $rD)"
Check "rim: the row scales it" ([math]::Abs($C::RimAt(0.5, 0.5) - 0.5 * $r0) -lt 1e-6)
$wRim = $C::EdgeWiden(0.001, $soft, $r0); $wCore = $C::EdgeWiden(1.0, $soft, $r0)
Check "rim: widest at the rim, the core's edge as before" ([math]::Abs($wRim - (1 + $r0)) -lt 0.01 -and $wCore -eq 1) "($wRim, $wCore)"
$fall = $true; $p = 1e9
foreach ($e in $excesses) { if ($e -le 0) { continue }; $w = $C::EdgeWiden([float]$e, [float]$soft, $r0); if ($w -gt $p + 1e-7) { $fall = $false }; $p = $w }
Check "rim: never wider further in" $fall

# ---- the constants the shader is handed -----------------------------------------------------------------
Check "constants: span >= 1, gain >= span (at the widest level never thinner than before), widest level inside (0, 1)" `
    ($C::Span -ge 1 -and $C::Gain -ge $C::Span -and $C::Middle -gt 0 -and $C::Middle -lt 1) "(span $($C::Span), gain $($C::Gain), middle $($C::Middle))"

if ($failed -gt 0) { Write-Host "$failed FAILED"; exit 1 }
Write-Host "all passed"
