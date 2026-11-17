# Offline checks of Sky/ShadowCadence.cs -- how far the cloud shadows would be off if the shadow map last drawn
# stayed up (the wind, the sun's turn), which decides when CloudShadowMap draws again. The class has no Unity
# dependency, so the SOURCE is compiled directly.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-shadowcadence.ps1
#
# What it guards: an unmoved sun measures 0; the sun's measure at the map's middle is the old
# height x turn / sin^2(elevation) (CloudShadowMap.SunMovedShadows); far from the middle the turned frame the map
# is read through adds the distance x the turn; a roll about the sun's own direction is seen; the wind's measure
# takes the noise's faster drift; no shadows either way measures 0.

$source = Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\ShadowCadence.cs"
Add-Type -TypeDefinition (Get-Content $source -Raw)

$C = [VolumetricClouds.Sky.ShadowCadence]
$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

# Unity's Quaternion.Euler(x, y, z) as a matrix (left-handed: Ry * Rx * Rz), columns = right, up, forward.
function Rx([double]$a) { $c = [math]::Cos($a); $s = [math]::Sin($a); ,@(@(1, 0, 0), @(0, $c, -$s), @(0, $s, $c)) }
function Ry([double]$a) { $c = [math]::Cos($a); $s = [math]::Sin($a); ,@(@($c, 0, $s), @(0, 1, 0), @(-$s, 0, $c)) }
function Rz([double]$a) { $c = [math]::Cos($a); $s = [math]::Sin($a); ,@(@($c, -$s, 0), @($s, $c, 0), @(0, 0, 1)) }
function Mul($A, $B) {
    $R = @(@(0.0, 0.0, 0.0), @(0.0, 0.0, 0.0), @(0.0, 0.0, 0.0))
    for ($i = 0; $i -lt 3; $i++) { for ($j = 0; $j -lt 3; $j++) { $s = 0.0; for ($k = 0; $k -lt 3; $k++) { $s += $A[$i][$k] * $B[$k][$j] }; $R[$i][$j] = $s } }
    ,$R
}
function Euler([double]$x, [double]$y, [double]$z) { $d = [math]::PI / 180; Mul (Mul (Ry ($y * $d)) (Rx ($x * $d))) (Rz ($z * $d)) }
function FrameOf($M) {
    New-Object VolumetricClouds.Sky.ShadowCadence+Frame ($M[0][0], $M[1][0], $M[2][0], $M[0][1], $M[1][1], $M[2][1], $M[0][2], $M[1][2], $M[2][2])
}
# The game's sun (DayNightProperties.UpdateSun, IL): Euler(0, longitude, latitude) * Euler(hour x 15 - 90, 0, 0).
function GameSun([double]$hour, [double]$lat = 29, [double]$lon = 41.8) { FrameOf (Mul (Euler 0 $lon $lat) (Euler ($hour * 15 - 90) 0 0)) }

$bottom = 1300.0; $top = 2600.0; $reach = 8640.0; $lowest = 0.02

$f = GameSun 12.0
Check "unmoved sun: 0" ($C::SunShift($f, $f, $reach, $bottom, $top, $lowest) -lt 1e-6)
Check "the game's sun points down at noon" ($f.Fy -lt -0.5) ("forward.y " + $f.Fy.ToString("F3"))

# A turn of the game's sun by 0.01 degree of hour angle (it turns about its own right: no roll).
$g = GameSun (12.0 + 0.01 / 15)
$sinE = -$f.Fy
$turn = 0.01 * [math]::PI / 180
$old = $bottom * $turn / ($sinE * $sinE)
$mid = $C::Shift($f, $g, 0, 0, $bottom)
# In the middle the shift lies between a turn all in azimuth (height x turn / sin) and all in elevation (the old
# measure's height x turn / sin^2, its bound). At noon the sun moves across: the low end.
Check "middle of the map, noon: within the old bound, at its azimuth end" (($mid -le $old * 1.01) -and ($mid -ge $old * $sinE * 0.99)) ("{0:F3} m, bounds {1:F3}..{2:F3} m" -f $mid, ($old * $sinE), $old)
$f8 = GameSun 8.0; $g8 = GameSun (8.0 + 0.01 / 15); $s8 = -$f8.Fy
$old8 = $bottom * $turn / ($s8 * $s8); $mid8 = $C::Shift($f8, $g8, 0, 0, $bottom)
Check "middle of the map, 8 am: within the old bound" (($mid8 -le $old8 * 1.01) -and ($mid8 -ge $old8 * $s8 * 0.99)) ("{0:F3} m, bounds {1:F3}..{2:F3} m" -f $mid8, ($old8 * $s8), $old8)
$all = $C::SunShift($f, $g, $reach, $bottom, $top, $lowest)
Check "corners: larger (the frame read through turns about the middle)" ($all -gt 2 * $old) ("{0:F3} m over the map vs {1:F3} m in the middle" -f $all, $old)
Check "corners: about (height / sin + distance) x turn" ($all -lt ($top / $sinE + $reach * [math]::Sqrt(2)) * $turn / $sinE * 1.2) ("{0:F3} m" -f $all)

# At 0.4 deg a second (the game's clock at 1x) and 30 draws a second, the lag per draw vs an eighth of a texel.
$g30 = GameSun (12.0 + (0.4 / 30) / 15)
$lag = $C::SunShift($f, $g30, $reach, $bottom, $top, $lowest)
$limit = $C::Limit(26000, 2048)
Check "limit = 26000 / 2048 / 8" ([math]::Abs($limit - 26000.0 / 2048 / 8) -lt 1e-9) ("{0:F3} m" -f $limit)
Write-Host ("      (the game's clock at 1x, 0.4 deg/s: one 30 Hz interval moves the corners' shadows {0:F2} m, the middle's {1:F2} m; the limit is {2:F2} m)" -f $lag, $C::Shift($f, $g30, 0, 0, $bottom), $limit)

# A roll about the sun's own direction (not what the game does, but what a mod turning the sun could): seen at the corners.
$roll = 0.01
$h = FrameOf (Mul (Mul (Ry (0)) (Euler 0 0 0)) (Euler 0 0 0))
$base = Euler 50 30 0
$rolled = Mul $base (Rz ($roll * [math]::PI / 180))
$sr = $C::SunShift((FrameOf $base), (FrameOf $rolled), $reach, $bottom, $top, $lowest)
$expect = $reach * [math]::Sqrt(2) * $roll * [math]::PI / 180
Check "a roll about the sun's direction is seen (~ distance x roll)" ($sr -gt 0.5 * $expect) ("{0:F3} m, distance x roll {1:F3} m" -f $sr, $expect)

# The sun under the lowest elevation both times: no shadows either way, 0. Crossing it: a draw at once.
$n1 = GameSun 3.0; $n2 = GameSun 3.5
Check "night both times: 0" ($C::SunShift($n1, $n2, $reach, $bottom, $top, $lowest) -eq 0)
Check "sun crossing the lowest elevation: huge" ($C::SunShift($n1, $f, $reach, $bottom, $top, $lowest) -gt 1e30)

# ---- the wind -------------------------------------------------------------------------------------------------
Check "wind: the noise drifts 1.25x" ([math]::Abs($C::DriftShift(1.0, 0, 0, 0, 1.25) - 1.25) -lt 1e-12)
Check "wind: a cloud shift moves both by itself" ([math]::Abs($C::DriftShift(0, 0, 3.0, 4.0, 1.25) - 5.0) -lt 1e-12)
Check "wind: no move, 0" ($C::DriftShift(0, 0, 0, 0, 1.25) -eq 0)

if ($failed -gt 0) { Write-Host "$failed FAILED"; exit 1 }
Write-Host "all passed"
