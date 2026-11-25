# Offline check of the game-update safeguard (VolumetricClouds/GameUpdate.cs on ModsLibCS1's
# GameCheck), run against the BUILT mod DLL and the INSTALLED game's DLLs, no game needed:
#   - the requirement list (GameUpdate.Require) finds everything in this game: 0 problems (after a
#     game update, this is the first thing to run: it names what changed);
#   - GAMECHECK_FAIL=Volumetric Weather adds exactly one problem (the forced switch-off for testing),
#     another name none;
#   - every [HarmonyPatch] class's target is among what Require checks, and Patcher applies every
#     such class (a new patch cannot go unchecked or unapplied);
#   - GameCheck itself reports a missing member, a field of another type, changed parameter names
#     and a patch that throws, and undoes once.
#
#   dotnet build VolumetricClouds/VolumetricClouds.csproj
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-gamecheck.ps1 [-Dll <built dll>]
#
# Not covered, because it needs the game: the switch-off itself (the pop-up, the Loader standing
# down, the options page's one line, the settings and the save left untouched).

param(
    [string]$Dll = "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Mods\VolumetricClouds\VolumetricClouds.dll"
)

$ErrorActionPreference = "Stop"
$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"
$libs = Join-Path $PSScriptRoot "..\libs"

# Every assembly from its bytes (Smart App Control blocks LoadFrom of a fresh unsigned DLL), found by name.
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
public static class GcResolver
{
    public static string[] Dirs;
    static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    public static void Install() { AppDomain.CurrentDomain.AssemblyResolve += Resolve; }
    public static Assembly Load(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        Assembly a;
        if (Loaded.TryGetValue(name, out a)) return a;
        a = Assembly.Load(File.ReadAllBytes(path));
        Loaded[name] = a;
        return a;
    }
    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        Assembly a;
        if (Loaded.TryGetValue(name, out a)) return a;
        foreach (string dir in Dirs)
        {
            string path = Path.Combine(dir, name + ".dll");
            if (File.Exists(path)) return Load(path);
        }
        return null;
    }
}
"@

[GcResolver]::Dirs = @($managed, (Split-Path $Dll), $libs)
[GcResolver]::Install()
foreach ($n in "Assembly-CSharp", "ColossalManaged", "ICities", "UnityEngine") { [void][GcResolver]::Load((Join-Path $managed "$n.dll")) }
[void][GcResolver]::Load((Join-Path $libs "CitiesHarmony.Harmony.dll"))
$mod = [GcResolver]::Load((Resolve-Path $Dll).Path)
"      $Dll ($($mod.GetName().Version))"

$checkType = $mod.GetType("ModsLibCS1.GameCheck", $true)
$update = $mod.GetType("VolumetricClouds.GameUpdate", $true)
$require = $update.GetMethod("Require")
$targets = $update.GetField("HarmonyTargets").GetValue($null)
$asm = [GcResolver]::Load((Join-Path $managed "Assembly-CSharp.dll"))

$failed = 0
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { "ok    $what" } else { "FAIL  $what -- $detail"; $script:failed++ }
}

$script:lines = New-Object System.Collections.Generic.List[string]
$sink = [Action[string]] { param($s) $script:lines.Add($s) }
function NewCheck { $script:lines.Clear(); return [Activator]::CreateInstance($checkType, @("Volumetric Weather", $sink)) }

# 1. This game has everything.
$env:GAMECHECK_FAIL = $null
$c = NewCheck
$require.Invoke($null, @($c))
Check "the installed game has everything Volumetric Weather needs by name" (-not $c.Failed) ($script:lines -join " | ")
Check "Require found 5 Harmony targets" ($targets.Count -eq 5) "$($targets.Count)"

# 2. The forced failure: exactly one problem.
$env:GAMECHECK_FAIL = "volumetric weather"
$c = NewCheck
$require.Invoke($null, @($c))
$env:GAMECHECK_FAIL = $null
Check "GAMECHECK_FAIL=Volumetric Weather: one problem" ($c.Failed -and $c.Problems.Count -eq 1) ("$($c.Problems.Count): " + ($script:lines -join " | "))
$env:GAMECHECK_FAIL = "Another Mod"
$c = NewCheck
$require.Invoke($null, @($c))
$env:GAMECHECK_FAIL = $null
Check "GAMECHECK_FAIL naming another mod: none" (-not $c.Failed) ($script:lines -join " | ")

# 3. Drift: every [HarmonyPatch] class's target is checked, and Patcher applies every one.
$c = NewCheck
$require.Invoke($null, @($c))
$patchClasses = @()
$unchecked = @()
foreach ($t in $mod.GetTypes()) {
    $attrs = @([Reflection.CustomAttributeData]::GetCustomAttributes($t) | Where-Object { $_.Constructor.DeclaringType.FullName -eq "HarmonyLib.HarmonyPatch" })
    if ($attrs.Count -eq 0) { continue }
    $patchClasses += $t
    $declaring = $null; $name = $null; $argTypes = $null
    foreach ($a in $attrs) {
        foreach ($arg in $a.ConstructorArguments) {
            if ($arg.ArgumentType -eq [Type]) { $declaring = [Type]$arg.Value }
            elseif ($arg.ArgumentType -eq [string]) { $name = [string]$arg.Value }
            elseif ($arg.ArgumentType -eq [Type[]]) { $argTypes = [Type[]]@($arg.Value | ForEach-Object { [Type]$_.Value }) }
        }
    }
    $all = [Reflection.BindingFlags]"Instance, Static, Public, NonPublic, DeclaredOnly"
    if ($argTypes -ne $null) { $target = $declaring.GetMethod($name, $all, $null, $argTypes, $null) }
    else { $target = @($declaring.GetMethods($all) | Where-Object { $_.Name -eq $name })[0] }
    $found = $false
    foreach ($m in $targets) { if ($target -ne $null -and $m.Equals($target)) { $found = $true } }
    if (-not $found) { $unchecked += "$($t.Name) -> $($declaring.Name).$name" }
}
Check "every [HarmonyPatch] class's target is in Require ($($patchClasses.Count) classes)" ($unchecked.Count -eq 0 -and $patchClasses.Count -gt 0) ($unchecked -join "; ")

$patch = $mod.GetType("VolumetricClouds.Patcher", $true).GetMethod("Patch", [Reflection.BindingFlags]"Static, NonPublic")
$il = $patch.GetMethodBody().GetILAsByteArray()
$missingFromPatcher = @()
foreach ($t in $patchClasses) {
    $token = [BitConverter]::GetBytes([int]$t.MetadataToken)
    $hit = $false
    for ($i = 0; $i -le $il.Length - 5; $i++) {
        if ($il[$i] -eq 0xD0 -and $il[$i + 1] -eq $token[0] -and $il[$i + 2] -eq $token[1] -and $il[$i + 3] -eq $token[2] -and $il[$i + 4] -eq $token[3]) { $hit = $true; break }
    }
    if (-not $hit) { $missingFromPatcher += $t.Name }
}
Check "Patcher applies every [HarmonyPatch] class" ($missingFromPatcher.Count -eq 0) ($missingFromPatcher -join ", ")

# 4. GameCheck's own reports.
$inst = [Reflection.BindingFlags]"Instance, Public, NonPublic"
$weather = $asm.GetType("WeatherManager", $true)

$c = NewCheck
[void]$c.RequireField($weather, "m_noSuchField", $inst, $null)
Check "a missing field is a problem" ($c.Problems.Count -eq 1) ""

$c = NewCheck
[void]$c.RequireField($weather, "m_lightningQueue", $inst, [int])
Check "a field of another type is a problem" ($c.Problems.Count -eq 1 -and $script:lines[0] -match "is a") ($script:lines -join " | ")

$rainTypes = [Type[]]@([UnityEngine.Vector3], [bool])
$c = NewCheck
[void]$c.RequireMethod($weather, "SampleRainIntensity", $inst, $rainTypes, [string[]]@("pos", "ignoreWeather"))
Check "WeatherManager.SampleRainIntensity as the patch binds it" (-not $c.Failed) ($script:lines -join " | ")

$c = NewCheck
[void]$c.RequireMethod($weather, "SampleRainIntensity", $inst, $rainTypes, [string[]]@("pos", "ignore"))
Check "a renamed parameter is a problem" ($c.Problems.Count -eq 1 -and $script:lines[0] -match "'ignoreWeather' now, not 'ignore'") ($script:lines -join " | ")

$c = NewCheck
[void]$c.RequireMethod($weather, "SampleRainIntensity", $inst, [Type[]]@([float]), [string[]]@())
Check "other parameter types are a problem" ($c.Problems.Count -eq 1) ($script:lines -join " | ")

$c = NewCheck
$ok1 = $c.Patch("Good", [Action] { })
$ok2 = $c.Patch("Bad", [Action] { throw (New-Object System.MissingMethodException "WeatherManager", "Gone") })
$ok3 = $c.Patch("After", [Action] { })
$script:undone = 0
$u1 = $c.UndoIfFailed([Action] { $script:undone++ })
$u2 = $c.UndoIfFailed([Action] { $script:undone++ })
Check "a patch that throws is a problem; none applied after it; undone once" ($ok1 -and -not $ok2 -and -not $ok3 -and $u1 -and $u2 -and $script:undone -eq 1 -and $c.Problems.Count -eq 1) ("$ok1 $ok2 $ok3 $u1 $u2 $($script:undone) | " + ($script:lines -join " | "))

$c = NewCheck
$script:undone = 0
Check "nothing failed: nothing undone" (-not $c.UndoIfFailed([Action] { $script:undone++ }) -and $script:undone -eq 0) ""

""
if ($failed -gt 0) { "$failed FAILED"; exit 1 } else { "all passed" }
