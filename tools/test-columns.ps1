# Offline checks of Sky/CloudColumns.cs (the cloud column map's reader, 1.6.1), run against the BUILT mod DLL.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-columns.ps1 [-Dll <scratch build>]
#
# The bytes CloudColumnMapPass.cginc writes, decoded; lookups with the clouds' drift; where a stroke's top goes.
# (Whether the GPU writes them where this reads them -- orientation, values -- is the editor probe's: the real shader
# against the CPU twin, docs/probe-2026-11-20.)

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

"=== CloudColumns (the cloud column map, 1.6.1) ==="
$CT = $asm.GetType("VolumetricClouds.Sky.CloudColumns")
$colType = $asm.GetType("VolumetricClouds.Sky.CloudColumns+Column")
if ($CT -eq $null) { "  FAIL  no CloudColumns in $Dll"; exit 1 }
$decode = $CT.GetMethod("Decode", $flags)
$enc = $CT.GetMethod("EncodeTau", $flags)
$dec = $CT.GetMethod("DecodeTau", $flags)
$at = $CT.GetMethod("At")
$strikeTop = $CT.GetMethod("StrikeTop", $flags)
$inCloud = $CT.GetMethod("InCloud", $flags)

# 1. The optical depth's encoding (t / (t + 4) in 8 bits): within 6% from 0.1 to 50.
$worst = 0.0
foreach ($t in 0.1, 0.3, 1.0, 3.0, 8.0, 20.0, 50.0) {
    $b = [byte]$enc.Invoke($null, @([single]$t)); $back = [double]$dec.Invoke($null, @($b))
    $e = [Math]::Abs($back - $t) / $t; if ($e -gt $worst) { $worst = $e }
}
Check ("optical depth through its byte and back: worst {0:P1} off over 0.1..50 (<= 6%)" -f $worst) ($worst -le 0.06)
Check "no cloud decodes as 0" (([double]$dec.Invoke($null, @([byte]0))) -eq 0)

# 2. A synthetic 16x16 map over 1600 m centred at (1000, -2000), slab 1500..4500: a cloud over the half x > centre,
#    its underside rising with z, dense 120 m above it, its top at 3000 m; the other half clear.
$N = 16; $side = 1600.0; $cx = 1000.0; $cz = -2000.0; $bottom = 1500.0; $topY = 4500.0; $slab = $topY - $bottom
$bytes = New-Object byte[] ($N * $N * 4)
for ($j = 0; $j -lt $N; $j++) { for ($i = 0; $i -lt $N; $i++) {
    $k = ($j * $N + $i) * 4
    if ($i -ge $N / 2) {
        $under = 200 + 20 * $j
        $bytes[$k] = [byte]$enc.Invoke($null, @([single]6.0))
        $bytes[$k + 1] = [byte][Math]::Round($under / $slab * 255)
        $bytes[$k + 2] = [byte][Math]::Round(1500 / $slab * 255)
        $bytes[$k + 3] = [byte][Math]::Round(($under + 120) / $slab * 255)
    } else { $bytes[$k] = 0; $bytes[$k + 1] = 255; $bytes[$k + 2] = 0; $bytes[$k + 3] = 255 }
} }
$map = $decode.Invoke($null, [object[]]@([byte[]]$bytes.psobject.BaseObject, [int]$N, [single]$cx, [single]$cz, [single]$side, [single]$bottom, [single]$topY, [double]500.0, [double]-300.0))
Check "a whole map decodes" ($map -ne $null)
$bad = $bytes.Clone(); $bad[40] = 0; $bad[41] = 0; $bad[42] = 0; $bad[43] = 0
Check "a map with an all-zero texel (the shader drew nothing there) is refused" ($null -eq $decode.Invoke($null, [object[]]@([byte[]]$bad, [int]$N, [single]$cx, [single]$cz, [single]$side, [single]$bottom, [single]$topY, [double]0, [double]0)))

# 1.6.1 (the performance pass): the same map from the texels as GetPixels32 gives them, straight and into a map's own arrays.
$decodePx = $CT.GetMethod("DecodePixels", $flags)
$c32 = [UnityEngine.Color32]
$px = [Array]::CreateInstance($c32, $N * $N)
for ($q = 0; $q -lt $N * $N; $q++) { $px[$q] = New-Object UnityEngine.Color32 ($bytes[$q * 4], $bytes[$q * 4 + 1], $bytes[$q * 4 + 2], $bytes[$q * 4 + 3]) }
$fromPx = $decodePx.Invoke($null, [object[]]@($px, [int]$N, [single]$cx, [single]$cz, [single]$side, [single]$bottom, [single]$topY, [double]500.0, [double]-300.0))
$same = $fromPx -ne $null -and $fromPx.CentreX -eq $cx -and $fromPx.DriftZ -eq -300.0
$probe = [object[]]@([single]($cx + (12.5 / $N - 0.5) * $side), [single]($cz + (5.5 / $N - 0.5) * $side), [double]500.0, [double]-300.0, $null)
$probe2 = $probe.Clone()
[void]$at.Invoke($map, $probe); [void]$at.Invoke($fromPx, $probe2)
Check "the texels (GetPixels32's) read as the bytes do" ($same -and $probe[4].Under -eq $probe2[4].Under -and $probe[4].Tau -eq $probe2[4].Tau -and $probe[4].Dense -eq $probe2[4].Dense)
Check ("the log's numbers from the counted bytes: '{0}'" -f $map.Describe()) ($map.Describe() -eq $fromPx.Describe() -and $map.Describe().StartsWith("50% of columns cloudy, 50% thick"))

function Look($x, $z, $dx, $dz) {
    $args2 = [object[]]@([single]$x, [single]$z, [double]$dx, [double]$dz, $null)
    $ok = [bool]$at.Invoke($map, $args2)
    return @($ok, $args2[4])
}
$texel = $side / $N
# Texel (12, 5)'s centre: x = cx + (12.5/16 - 0.5) side, z = cz + (5.5/16 - 0.5) side.
$x = $cx + (12.5 / $N - 0.5) * $side; $z = $cz + (5.5 / $N - 0.5) * $side
$r = Look $x $z 500.0 -300.0
$c = $r[1]; $wantUnder = $bottom + [Math]::Round((200 + 20 * 5) / $slab * 255) / 255 * $slab
Check ("a texel's centre reads its own column (underside {0:F1} m, wanted {1:F1})" -f $c.Under, $wantUnder) ($r[0] -and [Math]::Abs($c.Under - $wantUnder) -lt 0.5 -and $c.Tau -gt 5 -and $c.Tau -lt 7)
# Drift: 140 m along +x and 60 m along -z since the map was drawn: the same cloud is now 140 m east, 60 m south.
$r2 = Look ($x + 140) ($z - 60) 640.0 -360.0
Check "the clouds' drift since the map was drawn is followed (the same column, moved with the wind)" ($r2[0] -and [Math]::Abs($r2[1].Under - $c.Under) -lt 0.01)
$r3 = Look ($cx + $side) $cz 500.0 -300.0
Check "outside the map: no answer (the caller falls back to the weather map)" (-not $r3[0])
# A cloud's edge (between texel 7, clear, and 8, cloud): the underside is the cloud's own, not dragged to the slab's top.
$xe = $cx + (8.0 / $N - 0.5) * $side; $ze = $cz + (5.5 / $N - 0.5) * $side
$re = Look $xe $ze 500.0 -300.0
Check ("at a cloud's edge the underside stays the cloud's ({0:F0} m) and the share of cloud says it is an edge ({1:F2})" -f $re[1].Under, $re[1].Cloud) ([Math]::Abs($re[1].Under - $wantUnder) -lt 1 -and [Math]::Abs($re[1].Cloud - 0.5) -lt 0.01)
$rc = Look ($cx - 400) $cz 500.0 -300.0
Check "a clear column has no underside" ($rc[0] -and [single]::IsNaN($rc[1].Under))

# 3. Where a stroke's top goes: inside the dense part, over the underside, under the top.
$okTop = $true; $okIn = $true
$rnd = New-Object System.Random 5
for ($q = 0; $q -lt 200; $q++) {
    $col = [Activator]::CreateInstance($colType)
    $u = 1600 + 600 * $rnd.NextDouble(); $th = 50 + 1500 * $rnd.NextDouble()
    $colType.GetField("Under").SetValue($col, [single]$u)
    $colType.GetField("Top").SetValue($col, [single]($u + $th))
    $colType.GetField("Dense").SetValue($col, $(if ($rnd.NextDouble() -lt 0.8) { [single]($u + $th * 0.2 * $rnd.NextDouble()) } else { [single]::NaN }))
    $colType.GetField("Tau").SetValue($col, [single]5)
    $y = [double]$strikeTop.Invoke($null, @($col, $rnd.NextDouble()))
    $floor = [double]$CT.GetField("TopFloor").GetValue($null); $ceil = [double]$CT.GetField("TopCeiling").GetValue($null)
    if ($y -lt $u + $floor - 0.01 -or $y -gt [Math]::Max($u + $floor, $u + $th - $ceil) + 0.01) { $okTop = $false }
    $s = [double]$inCloud.Invoke($null, @($col, [single]$rnd.NextDouble()))
    if ($s -lt $u - 0.01 -or $s -gt $u + [Math]::Max($th, $floor) + 0.01) { $okIn = $false }
}
Check "a stroke's top: at least TopFloor into the cloud, under its top less TopCeiling (200 random columns)" $okTop
Check "a sheet's source: inside the cloud's body" $okIn
$none = [Activator]::CreateInstance($colType); $colType.GetField("Under").SetValue($none, [single]::NaN)
Check "no cloud: no top (NaN)" ([double]::IsNaN([double]$strikeTop.Invoke($null, @($none, [double]0.5))))

""
if ($failed -eq 0) { "ALL COLUMN MAP CHECKS PASSED" } else { "$failed CHECK(S) FAILED"; exit 1 }
