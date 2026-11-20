# Offline checks of Sky/ColumnCadence.cs (when the cloud column map is read back again, 1.6.1), run against the BUILT mod DLL.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-columncadence.ps1 [-Dll <scratch build>]
#
# The readback waits for the GPU (a hitch of tens of ms): never more often than every 6 s, and only when the map is no
# longer true -- the noises slid through it, the cover or the density moved, the camera went far, something changed at once.

param(
    [string]$Dll = "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Mods\VolumetricClouds\VolumetricClouds.dll"
)

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"
[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
[void][System.Reflection.Assembly]::LoadFrom("$managed\ColossalManaged.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$flags = [System.Reflection.BindingFlags]"Static,NonPublic,Public"
$failed = 0
function Check([string]$what, [bool]$ok) { if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failed++ } }

"=== ColumnCadence (the cloud column map's readback, 1.6.1) ==="
$C = $asm.GetType("VolumetricClouds.Sky.ColumnCadence")
if ($C -eq $null) { "  FAIL  no ColumnCadence in $Dll"; exit 1 }
$why = $C.GetMethod("Why", $flags)
$shift = $C.GetMethod("NoiseShift", $flags)
$noiseLimit = [double]$C.GetField("NoiseLimit").GetValue($null)
$coverLimit = [double]$C.GetField("CoverLimit").GetValue($null)
$floor = [double]$C.GetField("MinInterval").GetValue($null)
$ceiling = [double]$C.GetField("Ceiling").GetValue($null)
$camLimit = [double]$C.GetField("CameraLimit").GetValue($null)
# Why(haveMap, inputChanged, sinceRead, awakeSinceRead, noiseShift, coverChange, densityThen, densityNow, cameraOffset)
function W($have, $changed, $since, $awake, $noise, $cover, $d0, $d1, $cam) {
    $r = $why.Invoke($null, [object[]]@([bool]$have, [bool]$changed, [double]$since, [double]$awake, [double]$noise, [double]$cover, [double]$d0, [double]$d1, [double]$cam))
    if ($r -eq $null) { "-" } else { [string]$r }
}

"  limits: at least $floor s apart; noise $noiseLimit m, cover $coverLimit, camera $camLimit m, ceiling $ceiling s unpaused"
Check "the noises slide 0.25 x the drift (400 m of drift = 100 m)" ([Math]::Abs([double]$shift.Invoke($null, @([double]240, [double]-320, [double]1.25)) - 100) -lt 1e-9)
Check "no map yet (a storm starting): at once, whatever the floor" ((W $false $false 0 0 0 0 1 1 0) -eq "first")
Check "never two reads within the floor, whatever changed" ((W $true $true ($floor - 0.1) 1000 1e6 1 1 5 1e6) -eq "-")
Check "a setting, texture or variant changed: read" ((W $true $true $floor 0 0 0 1 1 0) -eq "input")
Check "the camera far from the map's middle: read" ((W $true $false 10 10 0 0 1 1 $camLimit) -eq "camera")
Check "the camera a little off: no read" ((W $true $false 10 10 0 0 1 1 ($camLimit - 1)) -eq "-")
Check "the noises slid the limit through the map: read (wind)" ((W $true $false 10 10 $noiseLimit 0 1 1 0) -eq "wind")
Check "a little less: no read" ((W $true $false 10 10 ($noiseLimit * 0.95) 0 1 1 0) -eq "-")
Check "the cover moved the limit (down too): read (cover)" (((W $true $false 10 10 0 $coverLimit 1 1 0) -eq "cover") -and ((W $true $false 10 10 0 (-$coverLimit) 1 1 0) -eq "cover"))
Check "the density 12% up: read (density); 5%: no read" (((W $true $false 10 10 0 0 1 1.12 0) -eq "density") -and ((W $true $false 10 10 0 0 1 1.05 0) -eq "-"))
Check "half the noise and half the cover together: read (the shares add)" ((W $true $false 10 10 ($noiseLimit * 0.5) ($coverLimit * 0.5) 1 1 0) -ne "-")
Check "nothing changed but the ceiling's unpaused time went by: read (ceiling)" ((W $true $false ($ceiling + 1) ($ceiling + 1) 0 0 1 1 0) -eq "ceiling")
Check "paused for an hour with nothing changing: no read" ((W $true $false 3600 5 0 0 1 1 0) -eq "-")

# His storm (2026-11-20 log: clouds drift ~21 m/s at 1x, the cover steady): how often.
$per = $noiseLimit / (21.0 * 0.25)
Check ("at his 21 m/s drift a read every {0:F0} s from the wind alone (60/min before), 3x speed every {1:F0} s" -f $per, ($per / 3)) ($per -ge $floor)

""
if ($failed -eq 0) { "ALL COLUMN CADENCE CHECKS PASSED" } else { "$failed CHECK(S) FAILED"; exit 1 }
