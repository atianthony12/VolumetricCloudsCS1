# Offline checks of Sky/CloudAuto.cs -- the Auto cloud style's curves. The class has no Unity dependency,
# so the SOURCES are compiled directly (with CloudSoft.cs, which it reads), no game and no built DLL needed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-auto.ps1
#
# What it guards: below the hand-over Auto IS Soft and above it Auto IS Cumulus, to the bit, in every
# number the renderer asks for; nothing jumps between them (every output moves by little per step of the
# cover); the heaps only come and the puffs only go, and the puffs go later than the heaps come (together,
# the sky emptied half way: render-detail.ps1 -Set auto-measure); the rain's cover is never more than the
# cover and never outside what is drawn (invariant 2).

$ErrorActionPreference = "Stop"

$sources = @(
    (Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\CloudSoft.cs"),
    (Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\CloudAuto.cs")
)
Add-Type -Path $sources

$Auto = [VolumetricClouds.Sky.CloudAuto]
$S = [VolumetricClouds.Sky.CloudSoft]
$look = $S::Default
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

$N = 1000
$amounts = @(1.0, 0.5, 0.0)

# ---- the order of the band ---------------------------------------------------------------------------

Check "the band: heaps start first, puffs go last, both inside 0..1" (
    $Auto::HeapsFrom -gt 0 -and $Auto::HeapsFrom -lt $Auto::PuffsFrom -and $Auto::HeapsTo -gt $Auto::PuffsFrom -and
    $Auto::HeapsTo -lt $Auto::PuffsTo -and $Auto::PuffsTo -lt 1) "($($Auto::HeapsFrom)-$($Auto::HeapsTo) / $($Auto::PuffsFrom)-$($Auto::PuffsTo))"

# ---- exactly Soft below, exactly Cumulus above --------------------------------------------------------

$bad = ""
foreach ($amount in $amounts) {
    for ($i = 0; $i -le $N; $i++) {
        $c = [single]($i / [double]$N)
        if ($c -gt $Auto::HeapsFrom) { break }
        $a = [single]$amount
        if ($Auto::HeapShare($c) -ne 0) { $bad = "heaps at $c" }
        if ($Auto::PuffFade($c) -ne 0) { $bad = "puffs fading at $c" }
        if ($Auto::PuffIntensity($c) -ne $c) { $bad = "puff intensity at $c" }
        if ($Auto::LayerCover($look, $c, $a) -ne $S::LayerCover($look, $c, $a)) { $bad = "layer at $c, row $a" }
        if ($Auto::DrawnCoverage($look, $c, $a) -ne $S::DrawnCoverage($look, $c, $a)) { $bad = "rain's cover at $c, row $a" }
        if ($Auto::LightScale($look, $c, $a) -ne $S::LightScaleAt($look, $S::LightShare($look, $c, $a))) { $bad = "light at $c, row $a" }
        if ($Auto::FragmentFactor($look, $c, $a) -ne ($S::LayerShare($look, $c) * $a)) { $bad = "fragments at $c, row $a" }
    }
}
Check "up to the band: Soft's numbers exactly" ($bad -eq "") $bad

$bad = ""
foreach ($amount in $amounts) {
    for ($i = $N; $i -ge 0; $i--) {
        $c = [single]($i / [double]$N)
        if ($c -lt $Auto::PuffsTo) { break }
        $a = [single]$amount
        if ($Auto::HeapShare($c) -ne 1) { $bad = "heaps not all there at $c" }
        if ($Auto::PuffIntensity($c) -ne 0) { $bad = "puffs left at $c" }
        if ($Auto::PuffsAt($c)) { $bad = "PuffsAt at $c" }
        if ($Auto::LayerCover($look, $c, $a) -ne ($c * $a)) { $bad = "layer at $c, row $a" }
        if ($Auto::DrawnCoverage($look, $c, $a) -ne $c) { $bad = "rain's cover at $c, row $a" }
        if ($Auto::LightScale($look, $c, $a) -ne 1) { $bad = "light at $c, row $a" }
        if ($Auto::FragmentFactor($look, $c, $a) -ne 1) { $bad = "fragments at $c, row $a" }
    }
}
Check "past the band: Cumulus's numbers exactly" ($bad -eq "") $bad

Check "heaps need no read at the band's start, puffs none at its end" (
    -not $Auto::HeapsAt([single]$Auto::HeapsFrom) -and $Auto::HeapsAt([single]($Auto::HeapsFrom + 0.001)) -and
    -not $Auto::PuffsAt([single]$Auto::PuffsTo) -and $Auto::PuffsAt([single]($Auto::PuffsTo - 0.001)))

# ---- nothing jumps ------------------------------------------------------------------------------------

$limit = 0.012   # per 0.1% of cover: the steepest smoothstep here (1.5 / 0.16 per unit) plus a margin
$outputs = @{
    "heaps"     = { param($c, $a) $Auto::HeapShare($c) }
    "puffs"     = { param($c, $a) $Auto::PuffFade($c) }
    "layer"     = { param($c, $a) $Auto::LayerCover($look, $c, $a) }
    "light"     = { param($c, $a) $Auto::LightScale($look, $c, $a) }
    "rain"      = { param($c, $a) $Auto::DrawnCoverage($look, $c, $a) }
    "fragments" = { param($c, $a) $Auto::FragmentFactor($look, $c, $a) }
}
foreach ($name in ($outputs.Keys | Sort-Object)) {
    $f = $outputs[$name]; $worst = 0.0; $at = 0.0
    foreach ($amount in $amounts) {
        $prev = [double](& $f ([single]0.5) ([single]$amount))
        for ($i = 501; $i -le 960; $i++) {
            $c = [single]($i / 1000.0)
            $v = [double](& $f $c ([single]$amount))
            $d = [Math]::Abs($v - $prev)
            if ($d -gt $worst) { $worst = $d; $at = $c }
            $prev = $v
        }
    }
    Check "no jump: $name" ($worst -lt $limit) ("(most {0:F4} per 0.1% at {1:F3})" -f $worst, $at)
}

# ---- only one way --------------------------------------------------------------------------------------

$rising = $true; $falling = $true; $prevH = -1.0; $prevF = -1.0; $worst = ""
for ($i = 0; $i -le $N; $i++) {
    $c = [single]($i / [double]$N)
    $h = $Auto::HeapShare($c); $f = $Auto::PuffFade($c)
    if ($h -lt $prevH - 1e-6) { $rising = $false; $worst = "heaps fall at $c" }
    if ($f -lt $prevF - 1e-6) { $falling = $false; $worst = "puffs come back at $c" }
    $prevH = $h; $prevF = $f
}
Check "the heaps only come, the puffs only go" ($rising -and $falling) $worst

$later = $true
for ($i = 0; $i -le $N; $i++) {
    $c = [single]($i / [double]$N)
    if ($Auto::PuffFade($c) -gt $Auto::HeapShare($c) + 1e-6) { $later = $false }
}
Check "the puffs are never further gone than the heaps have come" $later

$h = $Auto::HeapIntensity([single]0.75, [single]0.8)
Check "heap intensity: a share of what Cumulus would use" ([Math]::Abs($h - 0.8 * $Auto::HeapShare([single]0.75)) -lt 1e-6) "($h)"

# ---- the rain falls from drawn cloud (invariant 2) -------------------------------------------------------

$bad = ""
for ($i = 0; $i -le $N; $i++) {
    $c = [single]($i / [double]$N)
    $rain = $Auto::DrawnCoverage($look, $c, [single]1)
    if ($rain -gt $c + 1e-6) { $bad = "over the cover at $c ($rain)" }
    $puffs = $S::PuffCoverage($look, $Auto::PuffIntensity($c))
    $layer = $Auto::LayerCover($look, $c, [single]1)
    if ($rain -gt [Math]::Max($puffs, $layer) + 1e-6) { $bad = "outside puffs and layer at $c ($rain vs $puffs, $layer)" }
}
Check "the rain's cover: never over the cover, inside puffs or layer at the Classic layer row's 100%" ($bad -eq "") $bad

""
if ($failed -gt 0) { Write-Host "$failed check(s) FAILED"; exit 1 }
Write-Host "All Auto checks passed."
