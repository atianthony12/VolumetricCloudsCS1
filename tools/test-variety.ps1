# Offline checks of Sky/CloudVariety.cs (cloud variety: same base, different tops) and the variety pattern in
# Sky/CloudMix.cs. Pure classes: the SOURCES are compiled directly, no game and no built DLL needed.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-variety.ps1
#
# What it guards: row 0 is the heaps exactly as they are (stretch 1, no line move, no pull, the curve's height y / span
# to the bit); the mix map's existing channels (type, heaps) are bit for bit what they were before the variety pattern
# came (hashes recorded 2026-11-19, two seeds, scale 1 and 4); the pattern is even (a share of its values is that share
# of the sky); nothing jumps along the pattern; the bases never move with the bases held; the stretch never takes a heap
# over the top bound; the slab's and the empty-sky skip's "most" are never under any place's value; the lean is untouched
# where the sky already leans all the way (the hand-over to Cumulus).

$ErrorActionPreference = "Stop"

$sky = Join-Path $PSScriptRoot "..\VolumetricClouds\Sky"
$sources = @("ParallelRows.cs", "CloudSoft.cs", "CloudMix.cs", "CloudVariety.cs") | ForEach-Object { Join-Path $sky $_ }
Add-Type -Path $sources

$VT = [VolumetricClouds.Sky.CloudVariety]   # not $V: PowerShell names are case-blind and the loops use $v
$Mix = [VolumetricClouds.Sky.CloudMix]
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

$N = 2000
$towers = @(1.0, 1.3, $VT::TowerStretch)

# ---- row 0 = the heaps as they are ------------------------------------------------------------------------

$bad = ""
for ($i = 0; $i -le $N; $i++) {
    $v = [single]($i / [double]$N)
    if ($VT::Stretch($v, [single]0, [single]1.7) -ne 1) { $bad = "stretch at $v" }
    if ($VT::LineMove($v, [single]0) -ne 0) { $bad = "line at $v" }
}
foreach ($y in 0.0, 37.5, 512.25, 3999.0) {
    foreach ($span in 2000.0, 5400.0) {
        if ($VT::CurveHeight([single]$y, [single]$span, [single]0.15, [single]1) -ne [single]([single]$y / [single]$span)) { $bad = "curve height y $y span $span" }
    }
}
Check "row 0: stretch 1, no line move, the curve's height y / span to the bit" ($bad -eq "") $bad

# ---- the plateaus ----------------------------------------------------------------------------------------------

$bad = ""; $prev = -2.0; $jump = 0.0
for ($i = 0; $i -le $N; $i++) {
    $k = [double]$VT::Kind([single]($i / [double]$N))
    if ($k -lt $prev - 1e-6) { $bad = "Kind falls at $($i / $N)" }
    if ($i -gt 0) { $jump = [Math]::Max($jump, $k - $prev) }
    $prev = $k
}
Check "plateaus: calm -1 .. towers +1, never falling, no jump" ($bad -eq "" -and [double]$VT::Kind([single]0) -eq -1 -and [double]$VT::Kind([single]1) -eq 1 -and $jump -lt 0.02) "(largest step per 1/$N of the pattern $([Math]::Round($jump, 4)))"

$bad = ""
foreach ($row in 0.25, 0.5, 1.0) {
    foreach ($ts in $towers) {
        $ps = 0.0; $pl = -1.0; $ss = 0.0; $sl = 0.0
        for ($i = 0; $i -le $N; $i++) {
            $v = [single]($i / [double]$N)
            $s = [double]$VT::Stretch($v, [single]$row, [single]$ts); $l = [double]$VT::LineMove($v, [single]$row)
            if ($s -lt $ps - 1e-6) { $bad = "stretch falls at $v (row $row)" }
            if ($i -gt 0) { $ss = [Math]::Max($ss, [Math]::Abs($s - $ps)); $sl = [Math]::Max($sl, [Math]::Abs($l - $pl)) }
            if ($s -gt [double]$VT::MostStretch([single]$row, [single]$ts) + 1e-6) { $bad = "stretch over MostStretch at $v" }
            if ($l -gt [double]$VT::MostLineMove([single]$row) + 1e-6) { $bad = "line over MostLineMove at $v" }
            if ($s -le 0) { $bad = "stretch not positive at $v" }
            $ps = $s; $pl = $l
        }
        if ($ss -gt 0.01 -or $sl -gt 0.0005) { $bad = "a jump: stretch $ss, line $sl (row $row, towers $ts)" }
    }
}
Check "stretch and line: rising with vigour, no jumps, never over the slab's / skip's most" ($bad -eq "") $bad
$bad = ""
foreach ($s in 0.4, 1.0, 1.5, 2.2) {
    $prev = 0.0
    for ($i = 0; $i -le 100; $i++) {
        $c = [single]($i / 200.0)
        $a = [double]$VT::StretchAt([single]$s, $c)
        if ($s -le 1 -and $a -ne [double][single]$s) { $bad = "calm / middle stretch held back at $c" }
        if ($s -gt 1 -and ($a -lt 1 - 1e-6 -or $a -gt $s + 1e-6)) { $bad = "tower stretch out of 1..$s at $c" }
        if ($i -gt 0 -and $a -lt $prev - 1e-6) { $bad = "tower stretch falls with coverage at $c" }
        $prev = $a
    }
}
Check "a tower's stretch comes in with the heap's coverage (1 .. the stretch, rising); calm never held back" ($bad -eq "") $bad
Check "calm flatter, towers taller at 100%" ([double]$VT::Stretch([single]0, [single]1, [single]2) -lt 1 -and [double]$VT::Stretch([single]1, [single]1, [single]2) -gt 1 -and
    [double]$VT::LineMove([single]0, [single]1) -lt [double]$VT::LineMove([single]1, [single]1)) ""

# ---- the curve: bases held, top bounded -------------------------------------------------------------------------

$bad = ""
$hold0 = $VT::BaseHold
foreach ($w in 0.087, 0.15) {
    foreach ($s in 0.4, 1.3, 2.2) {
        $span = [single]5400
        foreach ($y in 0.0, 100.0, 300.0, 469.0, 800.0, 2500.0) {
            $h = [double]$VT::CurveHeight([single]$y, $span, [single]$w, [single]$s)
            if ($y / $span -le $w -and [Math]::Abs($h - $y / $span) -gt 1e-7) { $bad = "moved under the widest level: y $y w $w s $s" }
        }
        # continuous at the held level
        $a = [double]$VT::CurveHeight([single]($w * $span - 0.01), $span, [single]$w, [single]$s); $b = [double]$VT::CurveHeight([single]($w * $span + 0.01), $span, [single]$w, [single]$s)
        if ([Math]::Abs($a - $b) -gt 1e-5) { $bad = "a step at the widest level ($a / $b)" }
        # TopAt is where the stretched curve reaches the old top's height argument
        foreach ($top in 1000.0, 2500.0) {
            $t2 = [double]$VT::TopAt([single]$top, $span, [single]$w, [single]$s)
            $h2 = [double]$VT::CurveHeight([single]$t2, $span, [single]$w, [single]$s)
            if ([Math]::Abs($h2 - $top / $span) -gt 1e-5) { $bad = "TopAt / CurveHeight disagree: top $top w $w s $s" }
        }
    }
}
$VT::BaseHold = [single]0
$worst = 0.0
foreach ($s in 0.4, 1.3, 2.2) { foreach ($y in 10.0, 470.0, 1500.0, 3800.0) {
    $h = [double]$VT::CurveHeight([single]$y, [single]5400, [single]0.15, [single]$s); $o = $y / (5400.0 * $s)
    $worst = [Math]::Max($worst, [Math]::Abs($h - $o) / $o) } }
$VT::BaseHold = $hold0
Check "bases held (nothing moves under the widest level); BaseHold 0 = Cumulus height x the stretch" ($bad -eq "" -and $worst -lt 1e-6) "$bad (BaseHold 0 worst relative $([Math]::Round($worst, 9)))"

$bad = ""
foreach ($tallest in 900.0, 2300.0, 3055.0, 4400.0, 6000.0) {
    $ts = [double]$VT::TowerStretchFor([single]$tallest)
    if ($ts -lt 1 -or $ts -gt $VT::TowerStretch + 1e-6) { $bad = "towers' stretch $ts for $tallest" }
    $top = [double]$VT::TopAt([single]$tallest, [single]5400, [single]0.15, [single]$ts)
    if ($top -gt [Math]::Max($tallest, $VT::TopBound) + 1e-3) { $bad = "top $top over the bound for $tallest" }
}
Check "the top bound: towers never over it (or over today's tallest, if that is higher)" ($bad -eq "") $bad

# No pull (the author's call, 2026-11-19): the class has no lean of its own; the heaps stay where the style puts them.
Check "no pull: nothing in the class moves the lean" ($null -eq $VT.GetMethod("Lean") -and $null -eq $VT.GetMethod("PullAt")) ""

# ---- the shader's form ----------------------------------------------------------------------------------------------
# The uniforms CloudShaderParams sends hold the row already multiplied in (x row); the shader's sums must give the twin.
$bad = ""
foreach ($row in 0.3, 1.0) {
    $ts = [single]1.6; $r = [single]$row
    $calmDrop = [single]($r * (1 - $VT::CalmStretch)); $towerUp = [single]($r * ($ts - 1))
    $lc = [single]($r * $VT::CalmLine); $lt = [single]($r * $VT::TowerLine); $trim = [single]($r * $VT::LineTrim)
    for ($i = 0; $i -le 400; $i++) {
        $v = [single]($i / 400.0); $k = [single]$VT::Kind($v)
        $s = $(if ($k -lt 0) { 1 + $k * $calmDrop } else { 1 + $k * $towerUp })
        $m = $(if ($k -lt 0) { -$k * $lc } else { $k * $lt }) + $trim
        if ([Math]::Abs($s - [double]$VT::Stretch($v, $r, $ts)) -gt 1e-5) { $bad = "stretch at $v" }
        if ([Math]::Abs($m - [double]$VT::LineMove($v, $r)) -gt 1e-6) { $bad = "line at $v" }
    }
}
Check "the shader's form (row folded into the uniforms) = the twin" ($bad -eq "") $bad

# ---- the pattern --------------------------------------------------------------------------------------------------

$md5 = [Security.Cryptography.MD5]::Create()
function Hash([byte[]]$b) { return ([BitConverter]::ToString($md5.ComputeHash($b)) -replace '-', '') }
# Recorded 2026-11-19 from the source before the variety pattern was added (scratch hash-mix.ps1, "before").
$known = @{
    "12345-1" = @("80AF87ECD552788B5FF9DF46584173E5", "AF4E26BF01BE6ADC701B1D26DD5B5430")
    "12345-4" = @("E1CAF90ACD19229B027BD3CBD54D1F8C", "93B10B68D2AE139D5C865A3124251258")
    "23456-1" = @("0BF4A2F13F48542E85A196D12ACD94A7", "2DB4590EF0FF19BEC67BB2C4B6F8C131")
    "23456-4" = @("335B822F7699504ABB35633DD20C7670", "F83F37E0ED116070118038CBC84A2024")
}
$bad = ""; $shares = ""
$varHashes = @{}
foreach ($seed in 12345, 23456) {
    $sx = [single]0; $sz = [single]0; [void]$Mix::SeedOffsets($seed, [ref]$sx, [ref]$sz)
    foreach ($scale in 1, 4) {
        $size = $Mix::MapSizeFor($scale)
        $t = [byte[]]$Mix::BakeType($size, $sx, $sz, $scale); $h = [byte[]]$Mix::BakeHeaps($size, $sx, $sz, $scale)
        $want = $known["$seed-$scale"]
        if ((Hash $t) -ne $want[0]) { $bad += " type $seed x$scale" }
        if ((Hash $h) -ne $want[1]) { $bad += " heaps $seed x$scale" }
        $vb = [byte[]]$Mix::BakeVariety($size, $sx, $sz, $scale)
        $vb2 = [byte[]]$Mix::BakeVariety($size, $sx, $sz, $scale)
        if ((Hash $vb) -ne (Hash $vb2)) { $bad += " variety not repeatable $seed x$scale" }
        $varHashes["$seed-$scale"] = Hash $vb
        # Even: each sixteenth of the values holds a sixteenth of the map.
        $bins = New-Object int[] 16
        foreach ($x in $vb) { $bins[$x -shr 4]++ }
        foreach ($c in $bins) { if ([Math]::Abs($c / [double]$vb.Length - 1 / 16.0) -gt 0.01) { $bad += " uneven $seed x$scale" ; break } }
        $calm = 0; $tow = 0
        foreach ($x in $vb) { $k = [double]$VT::Kind([single]($x / 255.0)); if ($k -le -0.999) { $calm++ } elseif ($k -ge 0.999) { $tow++ } }
        $shares += " $seed x${scale}: calm $([Math]::Round(100.0 * $calm / $vb.Length))% towers $([Math]::Round(100.0 * $tow / $vb.Length))%;"
    }
}
if ($varHashes["12345-1"] -eq $varHashes["23456-1"]) { $bad += " two seeds draw one pattern" }
Check "mix map: type and heaps bit for bit as before (2 seeds x scale 1, 4); variety repeatable, even, its own per seed" ($bad -eq "") $bad
Check "plateau shares" ($shares -notmatch "calm (\d)%|towers 0%") $shares

Write-Host ""
if ($failed -gt 0) { Write-Host "$failed check(s) FAILED"; exit 1 }
Write-Host "all checks passed"
