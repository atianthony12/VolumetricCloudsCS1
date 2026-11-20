# Offline checks of Sky/WetMapRules.cs (wet ground only where it has rained, 1.6.1), run against the BUILT mod DLL.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-wetmap.ps1 [-Dll <scratch build>]
#
# The game's own wetness rule (WeatherManager.SimulationStepImpl) run per cell: N frames at once must be the game's
# frame-by-frame rule, a cell under the game's rain must be the game's number, and a cell the rain missed drier.

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

"=== WetMapRules (wet ground only where it has rained, 1.6.1) ==="
$R = $asm.GetType("VolumetricClouds.Sky.WetMapRules")
if ($R -eq $null) { "  FAIL  no WetMapRules in $Dll"; exit 1 }
$step = $R.GetMethod("Step", $flags)
$frames = $R.GetMethod("StepFrames", $flags)
$follow = $R.GetMethod("Follow", $flags)
$ratio = $R.GetMethod("Ratio", $flags)
$since = $R.GetMethod("FramesSince", $flags)
function S([double]$w, [double]$r, [uint32]$n) { [double]$step.Invoke($null, @([single]$w, [single]$r, [uint32]$n)) }
function F([double]$w, [double]$r, [uint32]$n) { [double]$frames.Invoke($null, @([single]$w, [single]$r, [uint32]$n)) }

# 1. N frames at once = the game's rule frame by frame (float rounding over thousands of frames: within 2e-4).
$worst = 0.0; $cases = 0
foreach ($w in 0.0, 0.05, 0.3, 0.7, 0.99, 1.0) {
    foreach ($r in 0.0, 0.01, 0.0164, 0.05, 0.2, 0.25, 0.6, 1.0) {
        foreach ($n in 1, 2, 7, 60, 600, 5000) {
            $d = [Math]::Abs((S $w $r $n) - (F $w $r $n)); if ($d -gt $worst) { $worst = $d }; $cases++
        }
    }
}
Check ("N frames at once = frame by frame: worst {0:E2} over {1} cases (<= 2e-4)" -f $worst, $cases) ($worst -le 2e-4)
Check "0 frames changes nothing (paused)" ((S 0.42 1.0 0) -eq [double][single]0.42)
Check "no rain: dries to 0 and stays (clamped at 0)" (((S 0.5 0.0 200000) -eq 0.0) -and ((F 0.0 0.0 10) -eq 0.0))
Check "the most rain: wets to 1 and stays (clamped at 1)" (((S 0.9 1.0 200000) -eq 1.0) -and ((F 1.0 1.0 10) -eq 1.0))
Check "rain over 25% counts as 25% (the game's cap)" ([Math]::Abs((S 0.3 0.25 500) - (S 0.3 0.9 500)) -lt 1e-7)
Check "more rain, wetter; less, drier" (((S 0.3 0.2 300) -gt (S 0.3 0.1 300)) -and ((S 0.3 0.0 300) -lt 0.3))

# 2. A cell under the game's rain all along is the game's number; the rule as the map runs it (rows visited in
#    uneven chunks of frames) against the game's frame by frame.
$rnd = New-Object System.Random 3
$game = 0.2; $cell = 0.2; $chunk = 0
$rainAt = { param($t) if ($t -lt 3000) { 0.18 } elseif ($t -lt 5000) { 0.6 } else { 0.0 } }
$t = 0
while ($t -lt 9000) {
    $k = 1 + $rnd.Next(0, 24)
    $r = & $rainAt $t
    $game = F $game $r $k
    $cell = [double]$follow.Invoke($null, @([single](S $cell $r $k), [single]$game))
    $t += $k
}
Check ("a cell under all the rain follows the game's number: {0:F5} vs {1:F5}" -f $cell, $game) ([Math]::Abs($cell - $game) -lt 2e-4)

# 3. Rained on vs missed: same game, one cell with the rain (share 1), one under a clearing (share 0).
$game = 0.0; $wet = 0.0; $dry = 0.0
for ($t = 0; $t -lt 4000; $t += 16) { $game = F $game 0.5 16; $wet = [double]$follow.Invoke($null, @([single](S $wet 0.5 16), [single]$game)); $dry = [double]$follow.Invoke($null, @([single](S $dry 0.0 16), [single]$game)) }
$rw = [double]$ratio.Invoke($null, @([single]$wet, [single]$game)); $rd = [double]$ratio.Invoke($null, @([single]$dry, [single]$game))
Check ("after rain: the game {0:P0}, the cell it rained on {1:P0} (share {2:F3}), the one it missed {3:P0} (share {4:F3})" -f $game, $wet, $rw, $dry, $rd) (($rw -gt 0.999) -and ($rd -lt 0.001))
# Then the rain stops: both dry by the same rule, the rained-on one stays the game's.
for ($t = 0; $t -lt 3000; $t += 16) { $game = F $game 0.0 16; $wet = [double]$follow.Invoke($null, @([single](S $wet 0.0 16), [single]$game)) }
Check ("drying after: the rained-on cell {0:P1} vs the game's {1:P1}" -f $wet, $game) ([Math]::Abs($wet - $game) -lt 2e-4)

# 4. Follow, Ratio, FramesSince.
Check "never wetter than the game (Follow = min)" ((([double]$follow.Invoke($null, @([single]0.8, [single]0.5))) -eq 0.5) -and (([double]$follow.Invoke($null, @([single]0.2, [single]0.5))) -eq [double][single]0.2))
Check "share 1 where the game's ground is dry (nothing to share)" (([double]$ratio.Invoke($null, @([single]0.0, [single]0.0))) -eq 1.0)
Check "share = cell / game, 0..1" (([Math]::Abs(([double]$ratio.Invoke($null, @([single]0.25, [single]0.5))) - 0.5) -lt 1e-6) -and (([double]$ratio.Invoke($null, @([single]0.9, [single]0.5))) -eq 1.0))
Check "frames since: plain" (([uint32]$since.Invoke($null, @([uint32]100, [uint32]160, [uint32]600000))) -eq 60)
Check "frames since: across the 2^32 wrap" (([uint32]$since.Invoke($null, @([uint32]4294967290, [uint32]10, [uint32]600000))) -eq 16)
Check "frames since: a jump back (a reset) counts as none" (([uint32]$since.Invoke($null, @([uint32]5000, [uint32]100, [uint32]600000))) -eq 0)

""
if ($failed -eq 0) { "ALL WET MAP CHECKS PASSED" } else { "$failed CHECK(S) FAILED"; exit 1 }
