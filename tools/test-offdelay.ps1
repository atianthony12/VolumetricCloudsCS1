# Offline checks of Sky/OffDelay.cs -- the switch that keeps the cloud shader's extras variant on for a few seconds
# after the light inside the clouds drops to 100% or under (CloudShaders.LightAskedNow), so it cannot flap. The
# class has no Unity dependency, so the SOURCE is compiled directly.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-offdelay.ps1

$source = Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\OffDelay.cs"
Add-Type -TypeDefinition (Get-Content $source -Raw)

$failed = 0
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

$d = New-Object VolumetricClouds.Sky.OffDelay
Check "starts off" (-not $d.On)
Check "unasked stays off" (-not $d.Update($false, 0.0, 5.0))
Check "asked: on at once" ($d.Update($true, 1.0, 5.0))
Check "unasked 4.9 s: still on" ($d.Update($false, 2.0, 5.0) -and $d.Update($false, 6.9, 5.0)) ("unasked for " + $d.Unasked(6.9))
Check "unasked 5 s: off" (-not $d.Update($false, 7.0, 5.0))
Check "off: Unasked 0" ($d.Unasked(8.0) -eq 0)

# Hovering: asked every other look, never 5 s in a row unasked -> never off (one change in all).
$d = New-Object VolumetricClouds.Sky.OffDelay
$changes = 0; $was = $false
for ($k = 0; $k -lt 6000; $k++) {
    $t = $k / 60.0
    $on = $d.Update(($k % 120) -lt 3, $t, 5.0)
    if ($on -ne $was) { $changes++; $was = $on }
}
Check "a value hovering about the line: one change in 100 s" ($changes -eq 1) "($changes)"

# Exactly at the line is unasked (the caller asks with Row > 0): it never comes on.
$d = New-Object VolumetricClouds.Sky.OffDelay
$any = $false
for ($k = 0; $k -lt 600; $k++) { if ($d.Update($false, $k / 60.0, 5.0)) { $any = $true } }
Check "never asked: never on" (-not $any)

# A clock that goes back (a new session's clock) does not keep it on forever.
$d = New-Object VolumetricClouds.Sky.OffDelay
[void]$d.Update($true, 100.0, 5.0)
[void]$d.Update($false, 101.0, 5.0)
[void]$d.Update($false, 2.0, 5.0)
Check "clock back: counts again from there" ($d.Update($false, 6.9, 5.0) -and -not $d.Update($false, 7.0, 5.0))

$d.Reset()
Check "reset: off" (-not $d.On)

if ($failed -gt 0) { Write-Host "$failed FAILED"; exit 1 }
Write-Host "all passed"
