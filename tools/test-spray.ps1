# Offline checks of Sky/WheelSprayRules.cs (Spray from wheels, 2026-11-15), run against the BUILT mod DLL.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-spray.ps1 [-Dll <scratch build>]
#
# Which wheels throw, from where, how much, and how each particle flies: pure maths, no game needed.

param(
    [string]$Dll = "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Mods\VolumetricClouds\VolumetricClouds.dll"
)

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Reflection;
public static class TestResolver
{
    public static string[] Dirs;
    [ThreadStatic] static bool busy;
    public static void Install() { AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        if (busy) return null;
        busy = true;
        try
        {
            string name = new AssemblyName(e.Name).Name;
            foreach (string dir in Dirs)
            {
                string path = Path.Combine(dir, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        }
        finally { busy = false; }
    }
}
"@
[TestResolver]::Dirs = @($managed, (Split-Path -Parent $Dll))
[TestResolver]::Install()

[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
[void][System.Reflection.Assembly]::LoadFrom("$managed\ColossalManaged.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$flags = [System.Reflection.BindingFlags]"Static,NonPublic,Public"
$failed = 0

function Check([string]$what, [bool]$ok) {
    if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failed++ }
}

"=== WheelSprayRules (Spray from wheels, 2026-11-15) ==="
$R = $asm.GetType("VolumetricClouds.Sky.WheelSprayRules")
$amount = $R.GetMethod("Amount", $flags)
$rear = $R.GetMethod("RearWheels", $flags)
$launch = $R.GetMethod("Launch", $flags)
$step = $R.GetMethod("Step", $flags)
$opacity = $R.GetMethod("Opacity", $flags)
$size = $R.GetMethod("Size", $flags)

function A([single]$speed, [single]$wet) { [object[]]$a = $speed, $wet; [single]$amount.Invoke($null, $a) }
Check "nothing below 4 m/s or on a road under 15% wet" (((A 3 1) -eq 0) -and ((A 20 0.1) -eq 0))
Check "more spray the faster and the wetter" (((A 10 1) -lt (A 20 1)) -and ((A 20 0.4) -lt (A 20 1)) -and ((A 30 1) -le 1.5))

# Rear wheels, driving +z at 10 m/s: both behind the middle; reversing (-z) puts them on the other side.
$pos = New-Object UnityEngine.Vector3 100, 5, 200
foreach ($dir in 1, -1) {
    $vel = New-Object UnityEngine.Vector3 0, 0, (10 * $dir)
    [object[]]$a = $pos.psobject.BaseObject, $vel.psobject.BaseObject, [single]2.6, [single]1.6, $null, $null
    [void]$rear.Invoke($null, $a)
    $l = $a[4]; $r = $a[5]
    $behind = (($l.z - $pos.z) * $dir -lt -1.2) -and (($r.z - $pos.z) * $dir -lt -1.2)
    $apart = [math]::Abs(($l - $r).magnitude - 1.6) -lt 0.01
    Check "rear wheels behind the way it drives ($dir), gauge apart" ($behind -and $apart)
}

# A droplet thrown at 20 m/s trails behind the car (slower than it), rises then falls.
$vel = New-Object UnityEngine.Vector3 0, 0, 20
[object[]]$la = [int]0, $vel.psobject.BaseObject, [single]0.5, [single]0.5, [single]0.5
$v = $launch.Invoke($null, $la)
Check "a droplet leaves slower than the car, going up" (($v.z -lt 20) -and ($v.z -gt 0) -and ($v.y -gt 0))
$wind = New-Object UnityEngine.Vector3 3, 0, 0
$p = 0.0; $vy = $v
for ($i = 0; $i -lt 60; $i++) { [object[]]$sa = [int]0, $vy, $wind.psobject.BaseObject, [single]0.0167; $vy = $step.Invoke($null, $sa) }
Check "after a second a droplet falls" ($vy.y -lt 0)

# Mist drifts onto the wind.
[object[]]$ma = [int]2, $vel.psobject.BaseObject, [single]0.5, [single]0.5, [single]0.5
$m = $launch.Invoke($null, $ma)
for ($i = 0; $i -lt 180; $i++) { [object[]]$sa = [int]2, $m, $wind.psobject.BaseObject, [single]0.0167; $m = $step.Invoke($null, $sa) }
Check "after 3 s the mist moves with the wind" (([math]::Abs($m.x - 3) -lt 0.5) -and ([math]::Abs($m.z) -lt 1))

$ok = $true
foreach ($k in 0, 1, 2) {
    foreach ($t in 0, 0.3, 0.7, 1) {
        [object[]]$oa = [int]$k, [single]$t; $o = [single]$opacity.Invoke($null, $oa)
        [object[]]$za = [int]$k, [single]$t; $z = [single]$size.Invoke($null, $za)
        if ($o -lt 0 -or $o -gt 1 -or $z -le 0) { $ok = $false }
    }
    [object[]]$end = [int]$k, [single]1; if ([single]$opacity.Invoke($null, $end) -ne 0) { $ok = $false }
}
Check "opacity 0..1 and gone at the end of a life, sizes above 0" $ok

""
if ($failed -eq 0) { "ALL CHECKS PASSED" } else { "$failed CHECK(S) FAILED"; exit 1 }
