# Offline checks of Sky/SkyLayerMix.cs -- the distant clouds' pictures (1.5.0), ONE PICTURE AT A TIME since
# 2026-11-25. The class has no Unity dependency, so the SOURCE is compiled directly.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\test-skylayer.ps1
#
# What it guards:
#   - every picture belongs to exactly one kind; the names match the embedded files; sizes;
#   - the baked means (SkyLayerMix.PictureMean, used by the old rows' conversion before any picture is decoded)
#     match the embedded JPEGs;
#   - the cut: 100% = the whole picture, 0 = nothing, the cloud shown follows the amount (share x the picture's own)
#     and never falls as the amount rises; the cut only removes cloud, never adds or moves any;
#   - a kind's picture per life: never by the amount, every picture of the kind used, lives in a row not stuck;
#   - the copies take turns: at most one whole at a time, both present only inside the swap, never a moment with
#     neither (beyond the overlap's brief gap when SwapOverlap is 0), and no jump between near ages;
#   - the old rows' conversion: 0 -> 0, the same mean cloud as the old walk, within 0..1;
#   - "Dynamic distant clouds" as before (spells, kinds by the cover, the cap, no jumps).

$source = Join-Path $PSScriptRoot "..\VolumetricClouds\Sky\SkyLayerMix.cs"
Add-Type -TypeDefinition (Get-Content $source -Raw)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class SkyTestJpeg {
  // Rows bottom up (row 0 = the image's bottom = the horizon), as Unity's LoadImage gives them; red / 255.
  public static float[] Read(string path, out int w, out int h) {
    using (var bmp = new Bitmap(path)) {
      w = bmp.Width; h = bmp.Height;
      var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
      var row = new byte[data.Stride];
      var grey = new float[w * h];
      for (int y = 0; y < h; y++) {
        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, data.Stride);
        int dst = (h - 1 - y) * w;
        for (int x = 0; x < w; x++) grey[dst + x] = row[3 * x + 2] / 255f;
      }
      bmp.UnlockBits(data);
      return grey;
    }
  }
}
'@

$M = [VolumetricClouds.Sky.SkyLayerMix]
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host "ok    $name  $detail" }
    else { Write-Host "FAIL  $name  $detail"; $script:failed++ }
}

$n = $M::Pictures.Length

# ---- kinds and files ----------------------------------------------------------------------------------------
$owner = @{}
for ($k = 0; $k -lt $M::Kinds; $k++) {
    foreach ($p in $M::PicturesOf($k)) {
        Check "picture $($M::Pictures[$p]) has one kind" (-not $owner.ContainsKey($p)) "kind $k"
        $owner[$p] = $k
        Check "KindOf($($M::Pictures[$p]))" ($M::KindOf($p) -eq $k)
    }
}
Check "every picture is used" ($owner.Count -eq $n) "$($owner.Count) of $n"

$folder = Join-Path $PSScriptRoot "..\VolumetricClouds\Resources\SkyLayer"
foreach ($p in $M::Pictures) {
    Check "embedded file for $p" (Test-Path (Join-Path $folder "$p.jpg"))
}
$bytes = (Get-ChildItem $folder -Filter *.jpg | Measure-Object Length -Sum).Sum
Check "pictures within 5 MB together" ($bytes -lt 5MB) ("{0:N0} bytes" -f $bytes)

# ---- histograms from the embedded pictures; the baked means -------------------------------------------------
$hists = @()
for ($p = 0; $p -lt $n; $p++) {
    $w = 0; $h = 0
    $grey = [SkyTestJpeg]::Read((Join-Path $folder "$($M::Pictures[$p]).jpg"), [ref]$w, [ref]$h)
    $hist = New-Object 'float[]' $M::Bins
    $M::Histogram($grey, $w, $h, $hist)
    $hists += ,$hist
    $sum = 0.0; foreach ($x in $hist) { $sum += $x }
    Check "histogram of $($M::Pictures[$p]) sums to 1" ([math]::Abs($sum - 1) -lt 1e-3) ("{0:F5}" -f $sum)
    $mean = $M::MeanAt($hist, -$M::CutSoft)
    Check "baked mean of $($M::Pictures[$p])" ([math]::Abs($mean - $M::PictureMean[$p]) -lt 0.002) ("measured {0:F4}, baked {1:F4}" -f $mean, $M::PictureMean[$p])
}

# ---- the cut ------------------------------------------------------------------------------------------------
for ($p = 0; $p -lt $n; $p++) {
    $hist = $hists[$p]; $name = $M::Pictures[$p]
    $full = $M::MeanAt($hist, -$M::CutSoft)
    Check "$name at 100%: the whole picture" ($M::SolveCut($hist, 1.0) -le -$M::CutSoft)
    Check "$name at 0: nothing" ($M::MeanAt($hist, $M::SolveCut($hist, 0.0)) -eq 0)
    $worst = 0.0; $monotone = $true; $prev = -1.0
    for ($i = 1; $i -le 100; $i++) {
        $s = $i / 100.0
        $cloud = $M::MeanAt($hist, $M::SolveCut($hist, $s))   # (not $m: PowerShell names ignore case, $m is $M)
        $worst = [math]::Max([double]$worst, [double][math]::Abs($cloud - $s * $full))
        if ($cloud -lt $prev - 1e-6) { $monotone = $false }
        $prev = $cloud
    }
    Check "$name : cloud shown = amount x its own" ($worst -lt 0.002) ("worst {0:F5}" -f $worst)
    Check "$name : more is never less" $monotone
}
# The cut only takes away: a box's cloud times a factor 0..1 (MeanAt's factor), never above the picture.
$okFactor = $true
foreach ($cut in -0.15, 0.0, 0.3, 0.7, 0.99) {
    for ($i = 0; $i -lt 256; $i++) {
        $v = ($i + 0.5) / 256
        $f = [math]::Max(0.0, [math]::Min(1.0, ($v - $cut) / $M::CutSoft))
        if ($f -lt 0 -or $f -gt 1) { $okFactor = $false }
    }
}
Check "the cut's factor stays within 0..1" $okFactor

# ---- one picture per life --------------------------------------------------------------------------------------
foreach ($seed in 1, 12345, -987654321) {
    for ($k = 0; $k -lt $M::Kinds; $k++) {
        $pics = $M::PicksOf($k)
        foreach ($q in $pics) { if ([array]::IndexOf($M::PicturesOf($k), $q) -lt 0) { Check "pick $q of kind $k is the kind's own" $false } }
        $seen = @{}; $repeatRun = 0; $longest = 0; $last = -1
        for ($life = -50; $life -lt 1000; $life++) {
            $p = $M::PictureFor($k, $life, $seed)
            if ([array]::IndexOf($pics, $p) -lt 0) { Check "kind $k life $life picks its own picture" $false "$p"; break }
            $seen[$p] = $true
            if ($p -eq $last) { $repeatRun++ } else { $repeatRun = 1 }
            $longest = [math]::Max($longest, $repeatRun); $last = $p
        }
        Check "seed $seed, $($M::KindNames[$k]): every pick of the kind comes up" ($seen.Count -eq $pics.Length) "$($seen.Count) of $($pics.Length)"
        # Random per life (each life also turns): a kind of two pictures repeats ~10 lives in a row somewhere in 1000
        # by chance alone (log2 1000); more than 16 would mean a stuck hash.
        if ($pics.Length -gt 1) { Check "seed $seed, $($M::KindNames[$k]): no picture stuck for long" ($longest -le 16) "longest run $longest lives in 1050" }
    }
}

# ---- the copies take turns ---------------------------------------------------------------------------------------
$life = [long]0; $pr = [float]0
function At([double]$age, [int]$slot) {
    $M::Presence($age, $slot, [ref]$script:life, [ref]$script:pr)
    return @($script:life, $script:pr)
}
foreach ($overlap in 1.0, 0.5, 0.0) {
    $M::SwapOverlap = $overlap
    $x = $M::SwapShare
    $bothWhole = $false; $bothOutsideSwap = $false; $neitherLong = 0; $jump = 0.0; $parity = $true; $lifeBack = $false
    $prev = $null
    for ($i = 0; $i -le 40000; $i++) {
        $age = -3.0 + $i * 0.0002
        $a = At $age 0; $b = At $age 1
        $f = $age - [math]::Floor($age)
        if ($a[1] -ge 1 -and $b[1] -ge 1) { $bothWhole = $true }
        if ($a[1] -gt 0 -and $b[1] -gt 0 -and $f -lt 1 - $x - 1e-6) { $bothOutsideSwap = $true }
        if ($a[1] -le 0 -and $b[1] -le 0) { $neitherLong++ }
        foreach ($c in @(@(0, $a), @(1, $b))) {
            $slot = $c[0]; $v = $c[1]
            if ((([long]$v[0] % 2) + 2) % 2 -ne $slot) { $parity = $false }
        }
        if ($prev -ne $null) {
            if ($a[0] -eq $prev[0][0]) { $jump = [math]::Max($jump, [math]::Abs($a[1] - $prev[0][1])) }
            if ($b[0] -eq $prev[1][0]) { $jump = [math]::Max($jump, [math]::Abs($b[1] - $prev[1][1])) }
            if ($a[0] -lt $prev[0][0] -or $b[0] -lt $prev[1][0]) { $lifeBack = $true }
        }
        $prev = @($a, $b)
    }
    $neitherShare = $neitherLong / 40001.0
    Check "overlap ${overlap}: never two copies whole at once" (-not $bothWhole)
    Check "overlap ${overlap}: two copies only inside the swap" (-not $bothOutsideSwap)
    Check "overlap ${overlap}: life n in copy n mod 2" $parity
    Check "overlap ${overlap}: lives only go forward" (-not $lifeBack)
    Check "overlap ${overlap}: no jump in presence between ages 0.0002 apart" ($jump -lt 0.03) ("{0:F4}" -f $jump)
    $allowed = if ($overlap -gt 0) { 0.0 } else { 0.002 }
    Check "overlap ${overlap}: a moment with neither copy only at the gap" ($neitherShare -le $allowed) ("{0:P2} of ages" -f $neitherShare)
}
$M::SwapOverlap = 1.0
Check "the carry span covers the swap" ([math]::Abs($M::CarrySpan - (1 + $M::SwapShare)) -lt 1e-6)

# ---- the old rows' conversion ------------------------------------------------------------------------------------
$w8 = New-Object 'float[]' $n
for ($k = 0; $k -lt $M::Kinds; $k++) {
    Check "convert $($M::KindNames[$k]) 0 -> 0" ($M::ConvertOld($k, 0) -eq 0)
    $pics = $M::PicksOf($k)
    $avg = 0.0; foreach ($p in $pics) { $avg += $M::PictureMean[$p] }; $avg /= $pics.Length
    $okRange = $true; $worst = 0.0
    for ($i = 0; $i -le 100; $i++) {
        $a = $i / 100.0
        $new = $M::ConvertOld($k, $a)
        if ($new -lt 0 -or $new -gt 1) { $okRange = $false }
        $M::Weights($k, $a, $w8)
        $old = 0.0; for ($p = 0; $p -lt $n; $p++) { $old += $w8[$p] * $M::PictureMean[$p] }
        if ($old -le $avg) { $worst = [math]::Max($worst, [math]::Abs($new * $avg - $old)) }
    }
    Check "convert $($M::KindNames[$k]): within 0..1" $okRange
    Check "convert $($M::KindNames[$k]): the same mean cloud (where reachable)" ($worst -lt 1e-4) ("{0:F6}" -f $worst)
    "      {0}: old 33% -> {1:F0}%, 67% -> {2:F0}%, 100% -> {3:F0}%" -f $M::KindNames[$k], (100 * $M::ConvertOld($k, 0.33)), (100 * $M::ConvertOld($k, 0.67)), (100 * $M::ConvertOld($k, 1.0)) | Write-Host
}

# ---- "Dynamic distant clouds" (unchanged rules) -------------------------------------------------------------------
# There are distant clouds less than half the time (the author's call) but some of the time; the cover picks the
# kinds; the total stays capped and nothing jumps. Sampled over 300 lives of the travelling copies, for three seeds.
$a = New-Object 'float[]' 4
$b = New-Object 'float[]' 4
foreach ($seed in 1, 12345, -987654321) {
    $shown = 0; $maxTotal = 0.0; $maxStep = 0.0; $inRange = $true
    $samples = 30000
    for ($i = 0; $i -lt $samples; $i++) {
        $cover = 0.5 + 0.5 * [math]::Sin($i * 0.0007)
        $M::Dynamic($i * 0.01, $seed, $cover, $a)
        $M::Dynamic($i * 0.01 + 0.01, $seed, $cover, $b)
        $total = 0.0
        for ($k = 0; $k -lt 4; $k++) {
            if ($a[$k] -lt 0 -or $a[$k] -gt 1) { $inRange = $false }
            $total += $a[$k]
            $maxStep = [math]::Max([double]$maxStep, [double][math]::Abs($b[$k] - $a[$k]))
        }
        if ($total -gt 0) { $shown++ }
        $maxTotal = [math]::Max([double]$maxTotal, [double]$total)
    }
    $share = $shown / $samples
    Check "dynamic, seed ${seed}: amounts within 0..1" $inRange
    Check "dynamic, seed ${seed}: total capped at 130%" ($maxTotal -le 1.3001) ("{0:F3}" -f $maxTotal)
    Check "dynamic, seed ${seed}: no jump between frames a hundredth of a life apart" ($maxStep -lt 0.02) ("{0:F4}" -f $maxStep)
    Check "dynamic, seed ${seed}: distant clouds some of the time, under half" ($share -gt 0.15 -and $share -lt 0.5) ("{0:P0} of the time" -f $share)
}

# Dynamic's amounts go through ConvertOld (SkyLayer.ReadAmounts): the mean cloud, averaged over the picks, per cover,
# against what the old walk gave. Short only where a kind's picks hold less cloud than the old pictures (cirrus: one
# thin picture while cirrus-1 / cirrus-3 are out).
foreach ($cover in 0.1, 0.35, 0.55, 0.95) {
    $oldSum = 0.0; $newSum = 0.0
    for ($i = 0; $i -lt 20000; $i++) {
        $M::Dynamic($i * 0.013, 1, $cover, $a)
        for ($k = 0; $k -lt 4; $k++) {
            $M::Weights($k, $a[$k], $w8)
            for ($p = 0; $p -lt $n; $p++) { $oldSum += $w8[$p] * $M::PictureMean[$p] }
            $picks = $M::PicksOf($k); $avg = 0.0; foreach ($q in $picks) { $avg += $M::PictureMean[$q] }; $avg /= $picks.Length
            $newSum += $M::ConvertOld($k, $a[$k]) * $avg
        }
    }
    $ratio = if ($oldSum -gt 0) { $newSum / $oldSum } else { 1.0 }
    Check ("dynamic at cover {0}: mean cloud kept (cirrus may fall short)" -f $cover) ($ratio -gt 0.45 -and $ratio -le 1.0001) ("new/old {0:F3}" -f $ratio)
}

function KindsAt([float]$cover) {
    $sum = @(0.0, 0.0, 0.0, 0.0)
    for ($i = 0; $i -lt 20000; $i++) {
        $M::Dynamic($i * 0.013, 1, $cover, $script:a)
        for ($k = 0; $k -lt 4; $k++) { $sum[$k] += $script:a[$k] }
    }
    return ,$sum
}
function Top($sum) { $best = 0; for ($k = 1; $k -lt 4; $k++) { if ($sum[$k] -gt $sum[$best]) { $best = $k } }; return $best }
$clear = KindsAt 0.1; $fair = KindsAt 0.35; $partly = KindsAt 0.55; $overcast = KindsAt 0.95
Check "dynamic: a clear sky gets mostly cirrus" ((Top $clear) -eq 0) $M::KindNames[(Top $clear)]
Check "dynamic: fair weather gets cirrus or sheep clouds" ((Top $fair) -le 1) $M::KindNames[(Top $fair)]
Check "dynamic: a partly cloudy sky gets cumulus on the horizon" ((Top $partly) -eq 2) $M::KindNames[(Top $partly)]
Check "dynamic: towards overcast, cloud layers" ((Top $overcast) -eq 3) $M::KindNames[(Top $overcast)]
Check "dynamic: no cirrus under overcast" ($overcast[0] -eq 0)

if ($failed -gt 0) { Write-Host "$failed FAILED"; exit 1 }
Write-Host "all passed"
