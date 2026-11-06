# Offline checks of Sky/CloudAuto.cs -- the Auto cloud style's curves. The class has no Unity dependency,
# so the SOURCES are compiled directly (with what it reads), no game and no built DLL needed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-auto.ps1
#
# What it guards: from its end Auto IS Cumulus, to the bit, in every number the renderer asks for; a sky
# that leans all the way to heaps meets that end; nothing jumps on the way (every output moves by little
# per step of the cover); the heaps only spread and the puffs only go, and the puffs go later (together, the
# sky emptied half way: render-detail.ps1 -Set auto-measure); the rain's cover is never more than the
# cover, keeps up with it, and is never outside what is drawn (invariant 2).

$ErrorActionPreference = "Stop"

$sources = @(
    (Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\ParallelRows.cs"),
    (Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\CloudSoft.cs"),
    (Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\CloudMix.cs"),
    (Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\CloudAuto.cs")
)
Add-Type -Path $sources

$Auto = [VolumetricClouds.Sky.CloudAuto]
$S = [VolumetricClouds.Sky.CloudSoft]
$Mix = [VolumetricClouds.Sky.CloudMix]
$look = $S::Default
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

$N = 1000
$amounts = @(1.0, 0.5, 0.0)

# ---- the order of the band ---------------------------------------------------------------------------

Check "the band: heaps spread first, puffs go last, both inside 0..1" (
    $Auto::SkyFrom -gt 0 -and $Auto::HeapsFrom -lt $Auto::PuffsFrom -and $Auto::HeapsTo -gt $Auto::PuffsFrom -and
    $Auto::HeapsTo -lt $Auto::PuffsTo -and $Auto::PuffsTo -lt 1) "($($Auto::SkyFrom)-$($Auto::HeapsTo) / $($Auto::PuffsFrom)-$($Auto::PuffsTo))"

# ---- a fair sky: mostly puffs, some heaps ------------------------------------------------------------

$bad = ""
for ($i = 0; $i -le $N; $i++) {
    $c = [single]($i / [double]$N)
    if ($c -gt $Auto::SkyFrom) { break }
    if ([Math]::Abs($Auto::HeapSky($c) - $Auto::FairHeapSky) -gt 1e-6) { $bad = "heaps' share at $c" }
    if ($Auto::PuffIntensity($c, 2) -ne $c) { $bad = "puffs at $c" }
    if ($Auto::LayerCover($look, $c, [single]1, 2) -ne $S::LayerCover($look, $c, [single]1)) { $bad = "layer in a puffs' stretch at $c" }
}
Check "fair weather: a tenth of the sky to heaps, a puffs' stretch is Soft's own" ($bad -eq "") $bad

# ---- exactly Cumulus above --------------------------------------------------------------------------

$bad = ""
foreach ($amount in $amounts) {
    for ($i = $N; $i -ge 0; $i--) {
        $c = [single]($i / [double]$N)
        if ($c -lt $Auto::PuffsTo) { break }
        $a = [single]$amount
        if ($Auto::HeapShare($c) -ne 1) { $bad = "heaps not all there at $c" }
        if ($Auto::HeapSky($c) -ne 1) { $bad = "not all the sky at $c" }
        foreach ($area in 0, 1, 2) {
            if ($Auto::PuffIntensity($c, $area) -ne 0) { $bad = "puffs left at $c" }
            if ($Auto::LayerCover($look, $c, $a, $area) -ne ($c * $a)) { $bad = "layer at $c, row $a, area $area" }
        }
        if ($Auto::PuffsAt($c)) { $bad = "PuffsAt at $c" }
        if ($Auto::DrawnCoverage($look, $c, $a) -ne $c) { $bad = "rain's cover at $c, row $a" }
        if ($Auto::LightScale($look, $c, $a) -ne 1) { $bad = "light at $c, row $a" }
        if ($Auto::FragmentFactor($look, $c, $a) -ne 1) { $bad = "fragments at $c, row $a" }
    }
}
Check "past the band: Cumulus's numbers exactly" ($bad -eq "") $bad

Check "the type is read up to the band's end, not from it" (
    -not $Auto::PuffsAt([single]$Auto::PuffsTo) -and $Auto::PuffsAt([single]($Auto::PuffsTo - 0.001)))

$bad = ""
foreach ($k in 0.0, 0.3, 0.7, 1.0) {
    if ($Mix::Lean([single]$k, [single]1) -ne 1) { $bad = "share 1 at $k" }
    if ($Mix::Lean([single]$k, [single]0) -ne 0) { $bad = "share 0 at $k" }
}
Check "at a share of 1 all of the sky leans to heaps, at 0 none: the band's ends meet the heaps' stretches" ($bad -eq "") $bad

# ---- nothing jumps ------------------------------------------------------------------------------------

$limit = 0.012   # per 0.1% of cover: the steepest smoothstep here (1.5 / 0.16 per unit) plus a margin
$outputs = @{
    "heaps' share"    = { param($c, $a) $Auto::HeapSky($c) }
    "puffs"           = { param($c, $a) $Auto::PuffFade($c) }
    "layer, type's 0" = { param($c, $a) $Auto::LayerCover($look, $c, $a, 0) }
    "layer, between"  = { param($c, $a) $Auto::LayerCover($look, $c, $a, 1) }
    "layer, type's 2" = { param($c, $a) $Auto::LayerCover($look, $c, $a, 2) }
    "light"           = { param($c, $a) $Auto::LightScale($look, $c, $a) }
    "rain"            = { param($c, $a) $Auto::DrawnCoverage($look, $c, $a) }
    "fragments"       = { param($c, $a) $Auto::FragmentFactor($look, $c, $a) }
}
foreach ($name in ($outputs.Keys | Sort-Object)) {
    $f = $outputs[$name]; $worst = 0.0; $at = 0.0
    foreach ($amount in $amounts) {
        $prev = [double](& $f ([single]0.2) ([single]$amount))
        for ($i = 202; $i -le 960; $i += 2) {
            $c = [single]($i / 1000.0)
            $v = [double](& $f $c ([single]$amount))
            $d = [Math]::Abs($v - $prev) / 2.0
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
    $h = $Auto::HeapSky($c); $f = $Auto::PuffFade($c)
    if ($h -lt $prevH - 1e-6) { $rising = $false; $worst = "heaps shrink at $c" }
    if ($f -lt $prevF - 1e-6) { $falling = $false; $worst = "puffs come back at $c" }
    $prevH = $h; $prevF = $f
}
Check "the heaps only spread, the puffs only go" ($rising -and $falling) $worst

$later = $true
for ($i = 0; $i -le $N; $i++) {
    $c = [single]($i / [double]$N)
    if ($Auto::PuffFade($c) -gt $Auto::HeapShare($c) + 1e-6) { $later = $false }
}
Check "the puffs are never further gone than the layer has come" $later

# ---- the rain falls from drawn cloud (invariant 2) -------------------------------------------------------

$bad = ""; $least = 1.0; $leastAt = 0.0
for ($i = 1; $i -le $N; $i++) {
    $c = [single]($i / [double]$N)
    $rain = $Auto::DrawnCoverage($look, $c, [single]1)
    if ($rain -gt $c + 1e-6) { $bad = "over the cover at $c ($rain)" }
    if ($c -ge 0.05 -and $rain / $c -lt $least) { $least = $rain / $c; $leastAt = $c }
    $most = 0.0
    foreach ($area in 0, 1, 2) {
        $most = [Math]::Max($most, [Math]::Max($S::PuffCoverage($look, $Auto::PuffIntensity($c, $area)), $Auto::LayerCover($look, $c, [single]1, $area)))
    }
    if ($rain -gt $most + 1e-6) { $bad = "outside puffs and layer at $c ($rain vs $most)" }
}
Check "the rain's cover: never over the cover, inside puffs or layer at the Classic layer row's 100%" ($bad -eq "") $bad
Check "the rain keeps up: the heavy sky is not Mixed's (35% at 70% cover)" ($least -gt 0.9) ("(least {0:P0} of the cover, at {1:F2})" -f $least, $leastAt)

""
if ($failed -gt 0) { Write-Host "$failed check(s) FAILED"; exit 1 }
Write-Host "All Auto checks passed."
