# Offline checks of Sky/RainLook.cs -- the numbers behind Wind on the rain (the streaks' lean, with its
# ceiling) and the rain's life (sheets, eddies, the light the drops throw back). The class has no Unity
# dependency, so the SOURCE is compiled directly.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-rainlook.ps1

$source = Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\RainLook.cs"
Add-Type -TypeDefinition (Get-Content $source -Raw)
$R = [VolumetricClouds.Sky.RainLook]

$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}
function Deg([double]$lean) { return [math]::Round([double]$R::Degrees([single]$lean), 1) }

# --- the wind's own lean: gentle in the weather's wind, steep only in a gale, with a ceiling ---------
Check "no wind, no lean" ($R::NaturalLean(0) -eq 0)
Check "a negative wind is no wind" ($R::NaturalLean(-5) -eq 0)
$prev = -1.0; $rises = $true
for ($v = 0.0; $v -le 60.0; $v += 0.25) { $l = [double]$R::NaturalLean($v); if ($l -lt $prev) { $rises = $false }; $prev = $l }
Check "never falls as the wind rises" $rises
Check "continuous at the fresh wind" ([math]::Abs([double]$R::NaturalLean(12.001) - [double]$R::NaturalLean(11.999)) -lt 0.001)
Check "the weather's strongest wind (12 m/s) leans under 25 deg" ((Deg $R::NaturalLean(12)) -lt 25) ("" + (Deg $R::NaturalLean(12)) + " deg")
Check "a gale (24 m/s) is steep: 50 deg or more" ((Deg $R::NaturalLean(24)) -ge 50) ("" + (Deg $R::NaturalLean(24)) + " deg")
Check "never past the ceiling" ([double]$R::NaturalLean(500) -eq [double]$R::Steepest) ("" + (Deg $R::NaturalLean(500)) + " deg")
Check "the ceiling is under the steepest drawn" ([double]$R::Steepest -lt [double]$R::SteepestDrawn)

# --- the row: 0% = the curtains' lean, 100% = the wind's, past 100% the wind read stronger -----------
foreach ($slant in 0.2, 0.33, 0.45) {
    Check "row 0% is the curtains' lean ($slant)" ([math]::Abs([double]$R::Lean(0, $slant, 18) - $slant) -lt 1e-6)
}
Check "row 100% is the wind's own" ([math]::Abs([double]$R::Lean(1, 0.45, 10) - [double]$R::NaturalLean(10)) -lt 1e-6)
Check "continuous at 100%" ([math]::Abs([double]$R::Lean(1.001, 0.45, 10) - [double]$R::Lean(0.999, 0.45, 10)) -lt 0.002)
Check "200% reads the wind 1.6 times as strong" ([math]::Abs([double]$R::Lean(2, 0.45, 10) - [double]$R::NaturalLean(16)) -lt 1e-5)
$steady = $true
for ($row = 0.0; $row -le 2.0; $row += 0.05) {
    for ($v = 0.0; $v -le 30.0; $v += 1.0) {
        $l = [double]$R::Lean($row, 0.45, $v)
        if ($l -lt 0 -or $l -gt [double]$R::Steepest + 1e-6) { $steady = $false }
    }
}
Check "every row, every wind: between upright and the ceiling" $steady

# In the weather's own rain (full rain, 10-12 m/s, the curtains at 0.45) no row under 100% leaves 20-25 deg.
$worst = 0.0; $least = 90.0
foreach ($row in 0, 0.15, 0.5, 1) { foreach ($v in 10.2, 11.8) {
    $d = Deg $R::Lean($row, 0.45, $v); if ($d -gt $worst) { $worst = $d }; if ($d -lt $least) { $least = $d } } }
Check "ordinary full rain, rows 0-100%: 18 to 25 deg" ($least -ge 18 -and $worst -le 25) "($least to $worst deg)"

# A gust at its strongest (gusts 0.9) on the steepest mean stays under the steepest drawn.
$peak = [double]$R::Steepest * (1 + [double]$R::GustShare(2, 0.9))
Check "the steepest gust stays under the steepest drawn" ($peak -lt [double]$R::SteepestDrawn) ("" + (Deg $peak) + " deg")
Check "a gust changes the lean by under a third" ([double]$R::GustShare(2, 5) -le 0.3001)
Check "no gusts at row 0" ($R::GustShare(0, 0.9) -eq 0 -and $R::SwingShare(0, 0.9) -eq 0 -and $R::Bands(0, 0.9) -eq 0)
Check "the swing is under 15 deg" ((Deg $R::SwingShare(2, 0.9)) -lt 15) ("" + (Deg $R::SwingShare(2, 0.9)) + " deg")

# --- the old maths, for the record: what "becoming horizontal" was -----------------------------------
function OldDeg([double]$row, [double]$wind, [double]$gusts) {
    $lean = $wind * $row / 7.5
    return [math]::Round([math]::Atan($lean * (1 + $gusts) / 0.8) * 180 / [math]::PI, 0)
}
Write-Host ("      (before: a gust in full rain at 11.8 m/s leaned small drops " + (OldDeg 0.15 11.8 0.68) + " / " + (OldDeg 0.5 11.8 0.68) + " / " + (OldDeg 1 11.8 0.68) + " deg at 15 / 50 / 100%; a gale 24 m/s at 100%: " + (OldDeg 1 24 0.9) + " deg)")

# --- the life ------------------------------------------------------------------------------------------
Check "the sheets cut half the drops in still air" ($R::SheetDepth(0, 0) -eq 0.5)
Check "never more than 85%" ([double]$R::SheetDepth(50, 0.9) -le 0.85)
Check "the sheets never stand still" ([double]$R::SheetSpeed(0) -ge 3)
Check "the sheets' wrap is a whole number of both patterns" ((($R::SheetWrap / 14) % 1) -eq 0 -and (($R::SheetWrap / 70) % 1) -eq 0 -and ($R::SheetWrap / 14) -eq 160 -and ($R::SheetWrap / 70) -eq 32)
# An eddy's steepest turn of the path: reach x 2 pi / 24 m (RainDrops.shader RAIN_EDDY_FALL).
$turn = [math]::Atan([double]$R::EddyReach(100) * 2 * [math]::PI / 24) * 180 / [math]::PI
Check "an eddy turns a streak under 13 deg in any wind" ($turn -lt 13) ("" + [math]::Round($turn, 1) + " deg")
Check "and at least 4 deg in still air" ([math]::Atan([double]$R::EddyReach(0) * 2 * [math]::PI / 24) * 180 / [math]::PI -ge 4)

# --- the light on the drops --------------------------------------------------------------------------
Check "streaks hidden (0%): no light either" ($R::LightShare(0) -eq 0)
Check "at 15% the drops still throw back over half" ([double]$R::LightShare(0.15) -gt 0.5) ("" + [math]::Round([double]$R::LightShare(0.15), 2))
Check "full from 100%" ($R::LightShare(1) -eq 1 -and $R::LightShare(2) -eq 1)
$prev = -1.0; $rises = $true
for ($o = 0.0; $o -le 2.0; $o += 0.01) { $s = [double]$R::LightShare($o); if ($s -lt $prev - 1e-6) { $rises = $false }; $prev = $s }
Check "never falls as the row rises" $rises

if ($failed -gt 0) { Write-Host "$failed FAILED"; exit 1 }
Write-Host "all passed"
