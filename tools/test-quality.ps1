# Offline checks of UI/QualityChoice.cs -- the quality preset a NEW install starts on (2026-11-18): Medium,
# Low where the game's graphics options were lowered or the card has under 4 GB, never High on its own --
# and of UI/NoticeRule.cs, which decides whether an existing player is shown the new-defaults notice.
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

"=== NoticeRule ==="
$rule = [VolumetricClouds.UI.NoticeRule]
Check "a new install (no file): not shown" (-not $rule::ShouldShow($false, ""))
Check "a file from before stamps (no ModVersion): shown" ($rule::ShouldShow($true, ""))
Check "a file stamped 1.5.3 (older than the defaults' version): shown" ($rule::ShouldShow($true, "1.5.3"))
Check "a file stamped $($rule::DefaultsChangedIn): not shown" (-not $rule::ShouldShow($true, $rule::DefaultsChangedIn))
Check "a file stamped 1.6.0 (the first round's defaults; 1.6.1 moved four more): shown" ($rule::ShouldShow($true, "1.6.0"))
Check "a file stamped later (1.6.2, 1.7.0): not shown" ((-not $rule::ShouldShow($true, "1.6.2")) -and (-not $rule::ShouldShow($true, "1.7.0")))
Check "an unreadable stamp: read as before stamps, shown" ($rule::ShouldShow($true, "abc"))
Check "a stamp with spaces: read" ((-not $rule::ShouldShow($true, " " + $rule::DefaultsChangedIn + " ")) -and $rule::ShouldShow($true, " 1.6.0 "))

""
if ($failed -eq 0) { "ALL CHECKS PASSED" } else { "$failed CHECK(S) FAILED"; exit 1 }
