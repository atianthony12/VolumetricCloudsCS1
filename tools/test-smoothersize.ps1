# Offline checks of Sky/SmootherSize.cs -- how large Smoother clouds marches the clouds (the row "Cloud drawing
# size", SmootherCloudsSize). The class has no Unity dependency, so the SOURCE is compiled directly.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-smoothersize.ps1
#
# What it guards: the default (0, half the screen) gives exactly the size Smoother clouds always made,
# (pixels + 1) / 2, at every size; "by screen size" is half up to 1440 lines and a third above; the sizes round up
# and are never 0; an unknown value (a newer file) is half.

$source = Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\SmootherSize.cs"
Add-Type -TypeDefinition (Get-Content $source -Raw)

$S = [VolumetricClouds.Sky.SmootherSize]
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

# ---- the default is the old size, bit for bit -------------------------------------------------------------
$bad = 0
foreach ($h in 1..5000) {
    $old = [math]::Max(1, [math]::Floor(($h + 1) / 2))
    if ($S::Size($h, $S::Divisor(0, $h)) -ne $old) { $bad++ }
}
Check "half = (pixels + 1) / 2 for 1..5000" ($bad -eq 0) "($bad differ)"

# ---- the choices ---------------------------------------------------------------------------------------------
Check "values append-only (0 half, 1 by screen, 2 third, 3 quarter)" (($S::Half -eq 0) -and ($S::ByScreen -eq 1) -and ($S::Third -eq 2) -and ($S::Quarter -eq 3))
Check "third divides by 3" ($S::Divisor(2, 2160) -eq 3 -and $S::Divisor(2, 720) -eq 3)
Check "quarter divides by 4" ($S::Divisor(3, 2160) -eq 4 -and $S::Divisor(3, 480) -eq 4)
Check "unknown value -> half" ($S::Divisor(7, 2160) -eq 2 -and $S::Divisor(-1, 2160) -eq 2)

# ---- by screen size --------------------------------------------------------------------------------------------
Check "by screen: 1080 lines -> half" ($S::Divisor(1, 1080) -eq 2)
Check "by screen: 1440 lines -> half (up to and with 1440)" ($S::Divisor(1, 1440) -eq 2)
Check "by screen: 1441 lines -> a third" ($S::Divisor(1, 1441) -eq 3)
Check "by screen: 2160 lines -> a third" ($S::Divisor(1, 2160) -eq 3)
$w4 = $S::Size(3840, $S::Divisor(1, 2160)); $h4 = $S::Size(2160, $S::Divisor(1, 2160))
$w1 = $S::Size(2560, $S::Divisor(1, 1440)); $h1 = $S::Size(1440, $S::Divisor(1, 1440))
Check "by screen: a 4K render marches what a 1440p one does" (($w4 -eq $w1) -and ($h4 -eq $h1)) "4K ${w4}x${h4}, 1440p ${w1}x${h1}"

# ---- the sizes -------------------------------------------------------------------------------------------------
Check "4K at a third = 1280x720" (($S::Size(3840, 3) -eq 1280) -and ($S::Size(2160, 3) -eq 720))
Check "4K at a quarter = 960x540" (($S::Size(3840, 4) -eq 960) -and ($S::Size(2160, 4) -eq 540))
Check "rounds up (1081 / 3 = 361)" ($S::Size(1081, 3) -eq 361)
Check "never 0 (1 pixel at a quarter = 1)" ($S::Size(1, 4) -eq 1 -and $S::Size(0, 4) -eq 1)
$cover = $true
foreach ($d in 2, 3, 4) { foreach ($p in 1..3000) { if ($S::Size($p, $d) * $d -lt $p) { $cover = $false } } }
Check "a target texel never covers less than its share (size x divisor >= pixels)" $cover
Check "divisor below 1 is taken as 1" ($S::Size(100, 0) -eq 100)

# ---- the log's words -------------------------------------------------------------------------------------------
Check "names" (($S::DivisorName(2) -eq "half") -and ($S::DivisorName(3) -eq "a third") -and ($S::DivisorName(4) -eq "a quarter"))
Check "choice names" (($S::ChoiceName(0) -eq "half the screen") -and ($S::ChoiceName(1) -like "by screen size*1440*"))

if ($failed -gt 0) { Write-Host "$failed FAILED"; exit 1 }
Write-Host "all passed"
