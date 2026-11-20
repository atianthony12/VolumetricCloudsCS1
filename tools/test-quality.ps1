# Offline checks of UI/QualityChoice.cs -- the quality preset a NEW install starts on (2026-11-18): Medium,
# Low where the game's graphics options were lowered or the card has under 4 GB, never High on its own --
# and of UI/NoticeRule.cs, which decides whether an existing player is shown the new-defaults notice, or a new
# install the welcome (1.6.1).
# Both have no Unity dependency, so the SOURCES are compiled directly.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-quality.ps1

$ui = Join-Path $PSScriptRoot "..\VolumetricClouds\UI"
Add-Type -TypeDefinition (Get-Content (Join-Path $ui "QualityChoice.cs") -Raw)
Add-Type -TypeDefinition (Get-Content (Join-Path $ui "NoticeRule.cs") -Raw)

$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

function Pick([int]$shadows, [int]$detail, [int]$textures, [int]$memory) {
    $why = ""
    $p = [VolumetricClouds.UI.QualityChoice]::Pick($shadows, $detail, $textures, $memory, [ref]$why)
    return @($p, $why)
}

"=== QualityChoice ==="
# The game's own defaults (OptionsGraphicsPanel / DefaultSettings: shadows 3, level of detail 2, textures 2).
$r = Pick 3 2 2 16000
Check "the game's defaults, a 16 GB card: Medium" ($r[0] -eq 1) $r[1]
$r = Pick 3 3 2 24000
Check "everything at the top: still Medium (never High on its own)" ($r[0] -eq 1) $r[1]
$r = Pick -1 -1 -1 0
Check "nothing readable: Medium" ($r[0] -eq 1) $r[1]
$r = Pick 3 2 2 2048
Check "a 2 GB card: Low" ($r[0] -eq 0) $r[1]
$r = Pick 3 2 2 4096
Check "a 4 GB card: Medium" ($r[0] -eq 1) $r[1]
$r = Pick 3 2 2 3072
Check "a 3 GB card: Low" ($r[0] -eq 0) $r[1]
$r = Pick 0 2 2 8000
Check "the game's shadows off: Low" ($r[0] -eq 0) $r[1]
$r = Pick 1 2 2 8000
Check "the game's shadows low: Low" ($r[0] -eq 0) $r[1]
$r = Pick 2 2 2 8000
Check "the game's shadows medium: Medium" ($r[0] -eq 1) $r[1]
$r = Pick 3 0 2 8000
Check "the game's level of detail low: Low" ($r[0] -eq 0) $r[1]
$r = Pick 3 1 2 8000
Check "the game's level of detail medium: Medium" ($r[0] -eq 1) $r[1]
$r = Pick 3 2 0 8000
Check "the game's textures low: Low" ($r[0] -eq 0) $r[1]

"=== the presets' drawing size and extras (1.6.1) ==="
$q = [VolumetricClouds.UI.QualityChoice]
# Sky.SmootherSize's values: Half 0, ByScreen 1, Third 2, Quarter 3.
Check "Low draws at a quarter, Medium a third, High half" (($q::DrawingSize(0) -eq 3) -and ($q::DrawingSize(1) -eq 2) -and ($q::DrawingSize(2) -eq 0))
Check "only Low draws no extras" ($q::ExtrasOff(0) -and (-not $q::ExtrasOff(1)) -and (-not $q::ExtrasOff(2)) -and (-not $q::ExtrasOff(3)))
function Reconcile([int]$preset, [int]$size) {
    $p = 0; $s = 0
    $changed = [VolumetricClouds.UI.QualityChoice]::Reconcile($preset, $size, [ref]$p, [ref]$s)
    return @($changed, $p, $s)
}
$r = Reconcile 2 0
Check "an older file on High, size never chosen: nothing changes" ((-not $r[0]) -and $r[1] -eq 2 -and $r[2] -eq 0)
$r = Reconcile 1 0
Check "an older file on Medium, size never chosen: a third, still Medium" ($r[0] -and $r[1] -eq 1 -and $r[2] -eq 2)
$r = Reconcile 0 0
Check "an older file on Low, size never chosen: a quarter, still Low" ($r[0] -and $r[1] -eq 0 -and $r[2] -eq 3)
$r = Reconcile 2 2
Check "High with a third chosen by hand: Custom, the size kept" ($r[0] -and $r[1] -eq 3 -and $r[2] -eq 2)
$r = Reconcile 0 1
Check "Low with 'by the screen' chosen by hand: Custom, the size kept" ($r[0] -and $r[1] -eq 3 -and $r[2] -eq 1)
$r = Reconcile 1 3
Check "Medium with a quarter chosen by hand: Custom, the size kept" ($r[0] -and $r[1] -eq 3 -and $r[2] -eq 3)
$r = Reconcile 3 0
Check "Custom: never touched" ((-not $r[0]) -and $r[1] -eq 3 -and $r[2] -eq 0)
$r = Reconcile 3 3
Check "Custom with a quarter: never touched" ((-not $r[0]) -and $r[1] -eq 3 -and $r[2] -eq 3)
$r = Reconcile 7 2
Check "a preset value from a newer version: never touched" ((-not $r[0]) -and $r[1] -eq 7 -and $r[2] -eq 2)
$again = $true
foreach ($preset in 0, 1, 2) {
    foreach ($size in 0, 1, 2, 3) {
        $first = Reconcile $preset $size
        $second = Reconcile $first[1] $first[2]
        if ($second[0]) { $again = $false }
    }
}
Check "a file is changed once: read again, nothing more changes (12 cases)" $again

"=== NoticeRule ==="
$rule = [VolumetricClouds.UI.NoticeRule]
$none = [VolumetricClouds.UI.NoticeRule+Kind]::None
$existing = [VolumetricClouds.UI.NoticeRule+Kind]::Existing
$welcome = [VolumetricClouds.UI.NoticeRule+Kind]::Welcome
Check "a new install (no file): the welcome (1.6.1)" ($rule::Decide($false, "") -eq $welcome)
Check "a new install that has not answered it yet (stamped '$($rule::NewInstall)'): the welcome again" ($rule::Decide($true, $rule::NewInstall) -eq $welcome)
Check "that stamp read whatever its case and spaces" ($rule::Decide($true, " New Install ") -eq $welcome)
Check "the stamp is no version (an older version reads it as none)" ($null -eq $rule::Parse($rule::NewInstall))
Check "a file from before stamps (no ModVersion): the notice" ($rule::Decide($true, "") -eq $existing)
Check "a file stamped 1.5.3 (older than the defaults' version): the notice" ($rule::Decide($true, "1.5.3") -eq $existing)
Check "a file stamped $($rule::DefaultsChangedIn): nothing" ($rule::Decide($true, $rule::DefaultsChangedIn) -eq $none)
Check "a file stamped 1.6.0 (the first round's defaults; 1.6.1 moved four more): the notice" ($rule::Decide($true, "1.6.0") -eq $existing)
Check "a file stamped later (1.6.2, 1.7.0): nothing" (($rule::Decide($true, "1.6.2") -eq $none) -and ($rule::Decide($true, "1.7.0") -eq $none))
Check "an unreadable stamp: read as before stamps, the notice" ($rule::Decide($true, "abc") -eq $existing)
Check "a stamp with spaces: read" (($rule::Decide($true, " " + $rule::DefaultsChangedIn + " ") -eq $none) -and ($rule::Decide($true, " 1.6.0 ") -eq $existing))

""
if ($failed -eq 0) { "ALL CHECKS PASSED" } else { "$failed CHECK(S) FAILED"; exit 1 }
