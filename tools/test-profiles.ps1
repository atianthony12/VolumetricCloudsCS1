# Offline checks of the profiles' pure parts, run against the BUILT mod DLL, no game needed:
#
#   dotnet build VolumetricClouds/VolumetricClouds.csproj
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-profiles.ps1
#
# ProfileName: the rules for a new profile's name, which IS its file's name -- Windows' rules on
# every system (a profile made on a Mac must open on Windows), names the same without regard to
# case, a ".xml" typed at the end dropped, and the first free "Profile N". IsFileName: a name
# from a hand edit of <Profile> can never reach outside the profiles folder. TextCut: a name cut for
# the bar, or a pasted colour, never keeps half of a character (an emoji is two UTF-16 units), which
# the game's font cannot draw; a name with half of one is not a name, and a file so named not listed.
# The file: a profile is written by the same SettingsXmlFormat as VolumetricClouds.xml, so a
# profile's text round-trips, and a broken or foreign one is refused as a FormatException, never
# anything else (Profiles catches exactly that and keeps the sky running).
#
# Not covered, because it needs the game: Profiles itself (the folder, the watcher, the saves).
# The log says what it did: "profiles:" at startup, "profile:" for every pick, add and remove.

param(
    [string]$Dll = "$env:LOCALAPPDATA\Colossal Order\Cities_Skylines\Addons\Mods\VolumetricClouds\VolumetricClouds.dll"
)

$managed = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\Managed"

Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Reflection;
public static class ProfileTestResolver
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
[ProfileTestResolver]::Dirs = @($managed, (Split-Path -Parent $Dll))
[ProfileTestResolver]::Install()

[void][System.Reflection.Assembly]::LoadFrom("$managed\UnityEngine.dll")
[void][System.Reflection.Assembly]::LoadFrom("$managed\ColossalManaged.dll")
$asm = [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($Dll))
$names = $asm.GetType('VolumetricClouds.ProfileName', $true)
$fmt = $asm.GetType('VolumetricClouds.SettingsXmlFormat', $true)
$entryType = $asm.GetType('VolumetricClouds.SettingsXmlFormat+Entry', $true)
# Internal: called through reflection, which reaches it where PowerShell's type syntax may not.
$cutType = $asm.GetType('VolumetricClouds.TextCut', $true)
$failed = 0

function Check([string]$name, $actual, $expected) {
    if ("$actual" -ceq "$expected") { Write-Host "ok    $name  ->  $actual" }
    else { Write-Host "FAIL  $name  ->  [$actual]  expected [$expected]"; $script:failed++ }
}

function Why([string]$typed) { $w = $names::Why($typed); if ($w -eq $null) { return 'usable' } else { return $w } }
function List([string[]]$items) { $l = New-Object 'System.Collections.Generic.List[string]'; foreach ($i in $items) { $l.Add($i) }; return ,$l }

# ---- the name rules -------------------------------------------------------------------------

Check "a plain name"                (Why "Storm")                 "usable"
Check "trimmed"                     ($names::Clean("  Storm  ")) "Storm"
Check "inner spaces kept"           ($names::Clean("Winter morning")) "Winter morning"
# Built from character codes: Windows PowerShell reads a script without a byte-order mark as ANSI.
$french = "Nuit d'" + [char]0x00E9 + "t" + [char]0x00E9 + " " + [char]0x00E0 + " Paris"
$japanese = -join ([char[]](0x5915, 0x713C, 0x3051, 0x306E, 0x7A7A))
Check "any language"                (Why $french)                 "usable"
Check "any script"                  (Why $japanese)               "usable"
Check "...and kept as typed"        ($names::Clean($french) -ceq $french) "True"
Check "a .xml typed is dropped"     ($names::Clean("Storm.xml"))  "Storm"
Check "...in any case"              ($names::Clean("Storm.XML ")) "Storm"
Check "...but a name that is only .xml is not a name" (Why ".xml") "Profiles.Invalid.Dot"
Check "empty"                       (Why "")                      "Profiles.Invalid.Empty"
Check "spaces only"                 (Why "    ")                  "Profiles.Invalid.Empty"
Check "null"                        (Why $null)                   "Profiles.Invalid.Empty"
Check "40 characters"               (Why ("a" * 40))              "usable"
Check "41 characters"               (Why ("a" * 41))              "Profiles.Invalid.Long"
Check "a slash"                     (Why "a/b")                   "Profiles.Invalid.Character"
Check "a backslash"                 (Why "a\b")                   "Profiles.Invalid.Character"
Check "a colon"                     (Why "C:storm")               "Profiles.Invalid.Character"
Check "a question mark"             (Why "why?")                  "Profiles.Invalid.Character"
Check "a star"                      (Why "*")                     "Profiles.Invalid.Character"
Check "angle brackets"              (Why "<b>")                   "Profiles.Invalid.Character"
Check "a quote"                     (Why 'say "hi"')              "Profiles.Invalid.Character"
Check "a pipe"                      (Why "a|b")                   "Profiles.Invalid.Character"
Check "a tab (control character)"   (Why "a`tb")                  "Profiles.Invalid.Character"
Check "a leading dot"               (Why ".hidden")               "Profiles.Invalid.Dot"
Check "a trailing dot"              (Why "storm.")                "Profiles.Invalid.Dot"
Check "a dot inside is fine"        (Why "v1.2 sky")              "usable"
Check "CON"                         (Why "CON")                   "Profiles.Invalid.Reserved"
Check "con, any case"               (Why "con")                   "Profiles.Invalid.Reserved"
Check "NUL with an extension"       (Why "nul.storm")             "Profiles.Invalid.Reserved"
Check "COM1"                        (Why "COM1")                  "Profiles.Invalid.Reserved"
Check "LPT9"                        (Why "lpt9")                  "Profiles.Invalid.Reserved"
Check "COM10 is not a device"       (Why "COM10")                 "usable"
Check "CONSOLE is not a device"     (Why "Console")               "usable"
# 1.5.0: a whole name in parentheses is the list's own ("(no profile)", "(Mod defaults)").
$fullWidth = [string][char]0xFF08 + "defaults" + [char]0xFF09
Check "(Mod defaults)"              (Why "(Mod defaults)")        "Profiles.Invalid.Parentheses"
Check "(no profile), spaced"        (Why "  (no profile) ")       "Profiles.Invalid.Parentheses"
Check "full-width parentheses"      (Why $fullWidth)              "Profiles.Invalid.Parentheses"
Check "parentheses inside are fine" (Why "Storm (heavy)")         "usable"
Check "one opening one is fine"     (Why "(draft")                "usable"
Check "a Clean that fails is null"  ($names::Clean("a/b") -eq $null) "True"

# ---- the same name, the next free one ----------------------------------------------------------

Check "same without regard to case" ($names::Same("Storm", "STORM")) "True"
Check "different names"             ($names::Same("Storm", "Storms")) "False"
Check "contains, any case"          ($names::Contains((List @("Clear", "storm")), "Storm")) "True"
Check "contains: not there"         ($names::Contains((List @("Clear")), "Storm")) "False"
Check "the first free"              ($names::NextFree((List @()), "Profile {0}")) "Profile 1"
Check "the next free"               ($names::NextFree((List @("Profile 1", "profile 2", "Profile 4")), "Profile {0}")) "Profile 3"
Check "another language's pattern"  ($names::NextFree((List @("Profil 1")), "Profil {0}")) "Profil 2"

# ---- a name that must stay inside the folder --------------------------------------------------

Check "a listed name"               ($names::IsFileName("Storm")) "True"
Check "an odd but real file name"   ($names::IsFileName("CON-ish sky (2)")) "True"
Check "empty is not a file"         ($names::IsFileName("")) "False"
Check "'.' is not a file"           ($names::IsFileName(".")) "False"
Check "'..' is not a file"          ($names::IsFileName("..")) "False"
Check "up and out, backslash"       ($names::IsFileName("..\VolumetricClouds")) "False"
Check "up and out, slash"           ($names::IsFileName("../VolumetricClouds")) "False"
Check "a drive"                     ($names::IsFileName("C:\Windows\win")) "False"

# Listed and picked: the pick is kept trimmed and written into VolumetricClouds.xml.
Check "listable: a plain name"      ($names::IsListable("Storm")) "True"
Check "listable: any language"      ($names::IsListable($french)) "True"
Check "not listable: a leading space (the pick would come back as 'Storm')" ($names::IsListable(" Storm")) "False"
Check "not listable: a trailing space" ($names::IsListable("Storm ")) "False"
Check "not listable: a control character (Linux allows it in a file name)" ($names::IsListable("Storm" + [char]1)) "False"
Check "not listable: a tab"         ($names::IsListable("a`tb")) "False"
Check "not listable: up and out"    ($names::IsListable("..\VolumetricClouds")) "False"

# ---- never half a character ---------------------------------------------------------------------
# An emoji is two UTF-16 units; a label holding one of them alone makes the game's font throw, and
# then no new text draws until a restart. Built from character codes (the script is read as ANSI);
# compared by length and by yes / no, so no lone half is ever printed.

$high = [char]0xD83C
$low = [char]0xDF05
$emoji = [string]$high + $low
function CutFirst($text, [int]$count) { return $cutType.GetMethod('First').Invoke($null, @($text, $count)) }
function CutLone($text) { return $cutType.GetMethod('HasLoneSurrogate').Invoke($null, @(, $text)) }
function CutWhole($text) { return $cutType.GetMethod('Whole').Invoke($null, @(, $text)) }
function EndsHigh([string]$s) { return $s.Length -gt 0 -and [char]::IsHighSurrogate($s[$s.Length - 1]) }

Check "first: plain text"           (CutFirst "Thunderstorm" 7)  "Thunder"
Check "first: shorter than the cut" (CutFirst "Storm" 40)        "Storm"
Check "first: exactly the cut"      (CutFirst "Storm" 5)         "Storm"
Check "first: null"                 ((CutFirst $null 5) -ceq "") "True"
Check "first: a count of 0"         ((CutFirst "Storm" 0) -ceq "") "True"
Check "first: a negative count"     ((CutFirst "Storm" -3) -ceq "") "True"
$across = "abcd" + $emoji + "ef"
$cut = CutFirst $across 5
Check "first: a cut between an emoji's halves keeps neither" ("" + $cut.Length + "," + (EndsHigh $cut)) "4,False"
$cut = CutFirst $across 6
Check "first: a cut right after a whole emoji keeps it" ("" + $cut.Length + "," + ($cut -ceq ("abcd" + $emoji))) "6,True"
$cut = CutFirst ($emoji + $emoji) 3
Check "first: the second of two emoji cut in half" ("" + $cut.Length + "," + ($cut -ceq $emoji)) "2,True"

Check "lone: plain text"            (CutLone "Storm")            "False"
Check "lone: null"                  (CutLone $null) "False"
Check "lone: empty"                 (CutLone "")                 "False"
Check "lone: a whole emoji"         (CutLone ("a" + $emoji + "b")) "False"
Check "lone: a first half alone"    (CutLone ("a" + $high + "b")) "True"
Check "lone: a first half at the end" (CutLone ("ab" + $high)) "True"
Check "lone: a second half alone"   (CutLone ("a" + $low + "b")) "True"
Check "lone: a second half first"   (CutLone ([string]$low + "ab")) "True"
Check "lone: the halves the wrong way round" (CutLone ([string]$low + $high)) "True"

Check "whole: plain text"           (CutWhole "Storm")           "Storm"
Check "whole: null"                 ((CutWhole $null) -ceq "") "True"
$kept = "Sky " + $emoji
Check "whole: a whole emoji is the same string" ([object]::ReferenceEquals((CutWhole $kept), $kept)) "True"
Check "whole: a first half alone"   (CutWhole ("a" + $high + "b")) "a?b"
Check "whole: a second half alone"  (CutWhole ("a" + $low + "b")) "a?b"
Check "whole: the wrong way round"  (CutWhole ([string]$low + $high)) "??"
$mixed = CutWhole ($emoji + $high)
Check "whole: a whole emoji kept beside a lone half" ("" + $mixed.Length + "," + ($mixed -ceq ($emoji + "?"))) "3,True"

# The name rules: half a character is not a name, and a file so named is not listed.
Check "a name with half an emoji"   (Why ("Sky " + $high))       "Profiles.Invalid.Character"
Check "a name with a second half alone" (Why ([string]$low + " sky")) "Profiles.Invalid.Character"
Check "a name with a whole emoji"   (Why ("Sky " + $emoji))      "usable"
Check "not listable: half an emoji" ($names::IsListable("Sky " + $high)) "False"
Check "not listable: halves the wrong way round" ($names::IsListable("Sky " + $low + $high)) "False"
Check "listable: a whole emoji"     ($names::IsListable("Sky " + $emoji)) "True"

# ---- a profile's file -------------------------------------------------------------------------

function Entry($section, $group, $name, $value, $comment) {
    $e = [Activator]::CreateInstance($entryType)
    $e.Section = $section; $e.Group = $group; $e.Name = $name; $e.Value = $value; $e.Comment = $comment
    return $e
}

function Parse([string]$text) {
    $version = 0
    $dups = New-Object 'System.Collections.Generic.List[string]'
    try {
        $values = $fmt::Parse($text, [ref]$version, $dups)
        return @{ Values = $values; Version = $version; Error = $null }
    }
    catch {
        $inner = $_.Exception
        while ($inner.InnerException -ne $null -and -not ($inner -is [System.FormatException])) { $inner = $inner.InnerException }
        return @{ Values = $null; Version = 0; Error = $inner }
    }
}

$list = [Activator]::CreateInstance([System.Collections.Generic.List``1].MakeGenericType($entryType))
$list.Add((Entry "In-game panel: Clouds tab" $null "CloudStyle" "1" "Cloud style. One of 0 (Classic), 1 (Cumulus); default 0."))
$list.Add((Entry "In-game panel: Clouds tab" "Colour" "CloudSunlitColor" "#FFE0C0" "Sunlit side. A colour, #RRGGBB; default #FFFFFF."))
$list.Add((Entry "In-game panel: Clouds tab" $null "CloudNightColors" "true" "Different colours at night. true or false, default false."))
$list.Add((Entry "In-game panel: Fog tab" $null "FogEnabled" "true" "Volumetric fog -- VERY heavy. true or false, default false."))
$header = "Volumetric Weather profile: a whole sky.`n`n- To share the sky, copy this file into the VolumetricCloudsProfiles folder -- of another computer."
$text = $fmt::Write($list, $header)

$r = Parse $text
Check "a profile's text parses back" ($r.Error -eq $null) "True"
if ($r.Values -ne $null) {
    Check "a choice by value"  $r.Values["CloudStyle"] "1"
    Check "a colour"           $r.Values["CloudSunlitColor"] "#FFE0C0"
    Check "a switch"           $r.Values["CloudNightColors"] "true"
    Check "the fog's switch"   $r.Values["FogEnabled"] "true"
    Check "four values"        $r.Values.Count 4
}

$r = Parse "<VolumetricClouds version=`"7`"><CloudStyle>1</CloudStyle><SomethingNew>3</SomethingNew></VolumetricClouds>"
Check "a newer version is read as far as it goes" ("" + $r.Version + "," + $r.Values["CloudStyle"] + "," + $r.Values["SomethingNew"]) "7,1,3"

$r = Parse "<VolumetricClouds><CloudStyle>1</CloudStyle>"
Check "half a file is refused as a FormatException" ($r.Error -is [System.FormatException]) "True"

$r = Parse "<RenderItConfig><Profiles /></RenderItConfig>"
Check "another mod's file is refused as a FormatException" ($r.Error -is [System.FormatException]) "True"

$r = Parse "not xml at all"
Check "not XML at all is refused as a FormatException" ($r.Error -is [System.FormatException]) "True"

# An editor may save with a byte-order mark (Notepad's "UTF-8 with BOM", or UTF-16). The mod
# reads every file with File.ReadAllText, which takes the mark off; so does this.
$tmp = [IO.Path]::GetTempFileName()
try {
    $xml = "<VolumetricClouds><CloudStyle>0</CloudStyle></VolumetricClouds>"
    [IO.File]::WriteAllText($tmp, $xml, (New-Object System.Text.UTF8Encoding($true)))
    $r = Parse ([IO.File]::ReadAllText($tmp))
    Check "a file saved as UTF-8 with a byte-order mark is read" ($(if ($r.Values) { $r.Values["CloudStyle"] } else { $r.Error.Message })) "0"
    [IO.File]::WriteAllText($tmp, $xml, [System.Text.Encoding]::Unicode)
    $r = Parse ([IO.File]::ReadAllText($tmp))
    Check "a file saved as UTF-16 is read" ($(if ($r.Values) { $r.Values["CloudStyle"] } else { $r.Error.Message })) "0"
}
finally { Remove-Item $tmp -ErrorAction SilentlyContinue }

Write-Host ""
if ($failed -eq 0) { Write-Host "all passed" } else { Write-Host "$failed FAILED"; exit 1 }
