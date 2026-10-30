# Rebuilds the cloud shader AssetBundles with Unity 5.6 -- one per platform -- checks what
# each one holds, and drops them where the mod embeds them. Run this after editing anything
# under UnityProject\Assets, then rebuild the mod.
#
#   .\build-bundle.ps1          # the WINDOWS bundle only: day to day (the author's call 2026-10-30, every project:
#                               # no Mac or Linux builds until release -- they took the build past 10 minutes)
#   .\build-bundle.ps1 -All     # Windows, Linux and Mac: for a release (release.ps1 refuses without it) and
#                               # before a Mac or Linux test build; then run tools\test-metal.ps1
#
# The bundles it does not build stay as they are. VolumetricClouds\Resources\bundles-built-from.txt records,
# per platform, the hash of the sources it was last built from (tools\bundle-sources.ps1), so a release can
# tell a stale Mac or Linux bundle. The editor is left on the Windows target (Windows is built last).
#
# What it checks, and why:
# - Unity's log for "Shader error" / "Shader warning": a shader that fails to compile still
#   produces a bundle (containing a broken shader) and exit code 0.
# - tools\bundle-apis.ps1 on EACH BUNDLE THAT SHIPS (they are built uncompressed for that
#   reason): every shader named in BundleBuilder.cs must be in it, with real code for every
#   graphics API of its platform. Unity leaves an EMPTY 4-byte blob, no message, exit 0, for
#   an API it cannot compile a shader for (Metal at `#pragma target 4.0`), and a bundle
#   without the API the game runs is a stand-down in every city.
# - Each bundle's hash before and after, printed: an edit confined to a .cginc once produced
#   a byte-identical stale bundle (ForceRebuild in BundleBuilder is what prevents it now, and
#   the printed state is how to see that it did). Bundle builds are deterministic, so
#   UNCHANGED after a shader edit is a problem and UNCHANGED without one is expected.

param(
    [switch]$All,
    [string]$Unity = "C:\Program Files\Unity\Editor\Unity.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "UnityProject"
$log = Join-Path $project "build.log"
$resources = Join-Path $root "VolumetricClouds\Resources"
$apisTool = Join-Path $root "tools\bundle-apis.ps1"
$builder = Join-Path $project "Assets\Editor\BundleBuilder.cs"

# One row per bundle: its name, and the platform ids every shader in it must carry
# (1 D3D9, 4 D3D11, 14 Metal, 15 OpenGLCore, 18 Vulkan). Must match BundleBuilder.Targets.
$allTargets = @(
    @{ Name = "win";   Platforms = "4,15";  Needs = "Direct3D 11 + OpenGL Core" },
    @{ Name = "linux"; Platforms = "15";    Needs = "OpenGL Core" },
    @{ Name = "mac";   Platforms = "14,15"; Needs = "Metal + OpenGL Core" }
)
$targets = if ($All) { $allTargets } else { @($allTargets | Where-Object { $_.Name -eq "win" }) }
$names = ($targets | ForEach-Object { $_.Name }) -join ","
$stampFile = Join-Path $resources "bundles-built-from.txt"
$sourcesTool = Join-Path $root "tools\bundle-sources.ps1"

if (-not (Test-Path $Unity)) { throw "Unity not found at $Unity" }
if (-not (Test-Path $apisTool)) { throw "Missing $apisTool" }
if (-not (Test-Path $builder)) { throw "Missing $builder" }
if (-not (Test-Path $sourcesTool)) { throw "Missing $sourcesTool" }

# The sources' hash BEFORE the build: what the bundles are built from (an edit saved during the build
# would make this stamp older than the sources, which the next release check would catch).
$sourcesHash = & powershell -NoProfile -ExecutionPolicy Bypass -File $sourcesTool
if ($sourcesHash -notmatch '^[0-9a-f]{64}$') { throw "tools\bundle-sources.ps1 gave no hash: $sourcesHash" }

# The shaders to expect: read out of BundleBuilder.cs, the one list, so a shader added there
# is checked here without anyone remembering a second list.
$shaders = @([regex]::Matches((Get-Content $builder -Raw), '"Assets/VolumetricClouds/(\w+)\.shader"') |
    ForEach-Object { "VolumetricClouds/" + $_.Groups[1].Value })
if ($shaders.Count -eq 0) { throw "No shader names found in $builder" }
Write-Host ("Expecting {0} shaders: {1}" -f $shaders.Count, ($shaders -join ", "))

$before = @{}
foreach ($t in $targets) {
    $f = Join-Path $resources ("volumetricclouds-" + $t.Name + ".bundle")
    $before[$t.Name] = if (Test-Path $f) { (Get-FileHash $f -Algorithm SHA256).Hash } else { $null }
}

if (Test-Path $log) { Remove-Item $log -Force }

Write-Host ("Building the {0} with Unity (headless)..." -f $(if ($All) { "three bundles" } else { "WINDOWS bundle only (-All for Linux and Mac too)" }))
# BundleBuilder sets the game's rendering for the build (legacy deferred tiers, linear colour space) and
# puts it back, but Unity rewrites the settings files as it saves them: they are put back byte for byte.
$settings = Join-Path $project "ProjectSettings"
$kept = Join-Path $env:TEMP ("vc-projectsettings-" + [guid]::NewGuid().ToString("N"))
Copy-Item $settings $kept -Recurse
try {
    $p = Start-Process -FilePath $Unity -Wait -PassThru -ArgumentList @(
        "-batchmode", "-nographics", "-quit",
        "-projectPath", "`"$project`"",
        "-buildTarget", "Win64",
        "-executeMethod", "BundleBuilder.Build",
        "-vcBundles", $names,
        "-logFile", "`"$log`"")
}
finally {
    Copy-Item (Join-Path $kept '*') $settings -Force
    Remove-Item -Recurse -Force $kept
}

if ($p.ExitCode -ne 0) {
    Get-Content $log -Tail 25
    throw "Unity exited with code $($p.ExitCode). See $log"
}

$shaderProblems = Select-String -Path $log -Pattern "Shader error", "Shader warning" -SimpleMatch
if ($shaderProblems) {
    $shaderProblems | ForEach-Object { Write-Host $_.Line -ForegroundColor Red }
    throw "A shader compiled with errors or warnings; nothing copied."
}

foreach ($t in $targets) {
    $marker = "BUNDLE BUILD OK: " + $t.Name
    if (-not (Select-String -Path $log -Pattern $marker -SimpleMatch -Quiet)) {
        throw "Marker '$marker' missing from the log. See $log"
    }
}

Write-Host "Checking what each bundle holds..."
foreach ($t in $targets) {
    $built = Join-Path $project ("Bundles\" + $t.Name + "\volumetricclouds")
    & powershell -NoProfile -ExecutionPolicy Bypass -File $apisTool -Bundle $built -Expect $t.Platforms -Shaders ($shaders -join ",")
    if ($LASTEXITCODE -ne 0) {
        throw ("The " + $t.Name + " bundle does not hold " + $t.Needs + " for every shader; nothing copied.")
    }
}

foreach ($t in $targets) {
    $src = Join-Path $project ("Bundles\" + $t.Name + "\volumetricclouds")
    $dst = Join-Path $resources ("volumetricclouds-" + $t.Name + ".bundle")
    Copy-Item $src $dst -Force
    $hash = (Get-FileHash $dst -Algorithm SHA256).Hash
    $state = if ($null -eq $before[$t.Name]) { "new" } elseif ($before[$t.Name] -eq $hash) { "UNCHANGED" } else { "changed" }
    Write-Host ("OK: {0} ({1} bytes, {2}) sha256 {3}" -f $dst, (Get-Item $dst).Length, $state, $hash) -ForegroundColor Green
}

# What each bundle was built from: this build's platforms get the sources' hash, the others keep theirs.
$stamps = [ordered]@{}
foreach ($t in $allTargets) { $stamps[$t.Name] = "(never recorded)" }
if (Test-Path $stampFile) {
    foreach ($line in Get-Content $stampFile) {
        if ($line -match '^(win|linux|mac)\s+(\S+)') { $stamps[$Matches[1]] = $Matches[2] }
    }
}
foreach ($t in $targets) { $stamps[$t.Name] = $sourcesHash }
$lines = @("# Written by build-bundle.ps1: per bundle, tools\bundle-sources.ps1's hash of the sources it was built from.",
           "# release.ps1 refuses a release unless every line matches the sources (build-bundle.ps1 -All).")
foreach ($k in $stamps.Keys) { $lines += "$k $($stamps[$k])" }
Set-Content -Path $stampFile -Value $lines -Encoding ASCII

foreach ($t in $allTargets) {
    if ($stamps[$t.Name] -ne $sourcesHash) {
        Write-Host ("NOTE: the {0} bundle was not built from these sources (left as it was): build-bundle.ps1 -All before a release." -f $t.Name) -ForegroundColor Yellow
    }
}

Write-Host "Now rebuild the mod to embed them."
