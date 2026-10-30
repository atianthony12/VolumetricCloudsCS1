# The shader bundles' sources as one hash: every .shader and .cginc under UnityProject\Assets\VolumetricClouds
# and Editor\BundleBuilder.cs (its shader list and settings), names and contents, line endings made LF so a
# checkout's autocrlf changes nothing. build-bundle.ps1 writes it, per platform it built, into
# VolumetricClouds\Resources\bundles-built-from.txt; release.ps1 refuses unless every platform's line
# matches it. Day-to-day builds are Windows only (source\repos\CLAUDE.md), so the Mac and Linux bundles go
# stale between releases: this is how a release knows to run build-bundle.ps1 -All first.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\bundle-sources.ps1        # prints the hash

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root "UnityProject\Assets"

$files = @(Get-ChildItem (Join-Path $assets "VolumetricClouds") -File | Where-Object { $_.Extension -eq ".shader" -or $_.Extension -eq ".cginc" })
$files += Get-Item (Join-Path $assets "Editor\BundleBuilder.cs")

$text = New-Object System.Text.StringBuilder
foreach ($f in ($files | Sort-Object Name)) {
    # (Worked out first: inside a method's brackets the comma of -replace would part two arguments.)
    $content = [System.IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n"
    [void]$text.Append($f.Name).Append("`n").Append($content).Append("`n")
}

$sha = [System.Security.Cryptography.SHA256]::Create()
$bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text.ToString()))
-join ($bytes | ForEach-Object { $_.ToString("x2") })
