param(
    # What to run: Asm (our compiled programs against the game's), Render (the Unity bench: the
    # rebuild against its game-only twin pixel for pixel, VW's wind against its C# twin), or All.
    [ValidateSet('All', 'Asm', 'Render')][string]$Part = 'All',
    [string]$Unity = "C:\Program Files\Unity\Editor\Unity.exe",
    [string]$GameAssets = "C:\Program Files (x86)\Steam\steamapps\common\Cities_Skylines\Cities_Data\resources.assets",
    # The disassembler that also reads each program's keywords (Real Light's, the sibling repo's: VW's own
    # tools\shaderdump.ps1 has no keyword index).
    [string]$ShaderDump = (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))) "RealLight\tools\shaderdump.ps1"),
    [string]$Work = (Join-Path $env:TEMP "vw-tree-bench"),
    # Print up to this many differing lines per program that is not the game's line for line.
    [int]$ShowLines = 12
)

# THE TREE BENCH. The near and billboard trees are drawn by our rebuilds of the game's tree shaders
# (UnityProject/Assets/VolumetricClouds/Trees*.shader, Sky/TreeShaders.cs).
#
# Asm: builds UnityProject's TreeBench bundle (TreeBench.Build: the two shaders and their game-only
# twins, TreeBenchGame*.shader = VW's wind compiled out), disassembles every D3D11 program of the twins
# and of the game's Custom/Trees/Default and Custom/Trees/Group (resources.assets), and sets each of
# ours against the game's program of the same kind and keywords (+ INFOMODE_OFF; the game has more
# passes per keyword set, the best match is taken). "same" = the instructions line for line.
#
# Render: TreeBenchRun in the 5.6 editor (with graphics): see UnityProject/Assets/Editor/TreeBenchRun.cs.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root "UnityProject"
New-Item -ItemType Directory -Force $Work | Out-Null

if (-not (Test-Path $ShaderDump)) { throw "No shaderdump.ps1 at $ShaderDump (-ShaderDump)" }

function Run-Unity([string]$method, [switch]$graphics) {
    $log = Join-Path $Work ($method + ".log")
    $args = @("-batchmode", "-quit", "-projectPath", "`"$project`"", "-executeMethod", $method, "-logFile", "`"$log`"")
    if (-not $graphics) { $args = @("-nographics") + $args }
    # The bench sets the game's rendering (legacy deferred tiers, linear, shadows) and Unity saves it on
    # quit; the project's own settings files are put back as they were, byte for byte.
    $settings = Join-Path $project "ProjectSettings"
    $kept = Join-Path $Work "ProjectSettings-kept"
    if (Test-Path $kept) { Remove-Item -Recurse -Force $kept }
    Copy-Item $settings $kept -Recurse
    try { $p = Start-Process -FilePath $Unity -Wait -PassThru -ArgumentList $args }
    finally { Copy-Item (Join-Path $kept '*') $settings -Force }
    if ($p.ExitCode -ne 0) {
        Get-Content $log -Tail 30
        throw "${method}: Unity exited with $($p.ExitCode) (log $log)"
    }
    return $log
}

# What each constant-buffer register, texture and sampler slot holds, from the names shaderdump puts
# at the head of each listing ("// cb0 $Globals (880 bytes): _Color [47], unity_ObjectToWorld (4x4) [0]",
# "// t3 _MainTex (s0)"): cbN[i] -> the name (a matrix's columns name.0..3, an array's first register
# its name[]), tN and sN -> the texture's name.
function Names([string[]]$text) {
    $map = @{}
    foreach ($line in $text) {
        if ($line -match '^// (cb\d+) [^:]+: (.*)$') {
            $cb = $Matches[1]
            foreach ($p in ($Matches[2] -split ', ')) {
                if ($p -notmatch '^(\S+?)(\[\d+\])? (\(4x4\) )?\[(\d+)\]') { continue }
                $n = $Matches[1]; $at = [int]$Matches[4]
                if ($Matches[3]) { for ($k = 0; $k -lt 4; $k++) { $map["$cb[$($at + $k)]"] = "$n.$k" } }
                elseif ($Matches[2]) { $map["$cb[+$at]"] = "$n[]" }
                # Several uniforms in one register (their components): all its names.
                elseif ($map.ContainsKey("$cb[$at]")) { $map["$cb[$at]"] = ((@($map["$cb[$at]"] -split '\|') + $n) | Sort-Object) -join '|' }
                else { $map["$cb[$at]"] = $n }
            }
        }
        elseif ($line -match '^// t(\d+) (\S+) \(s(\d+)') { $map["t$($Matches[1])"] = $Matches[2]; $map["s$($Matches[3])"] = $Matches[2] }
    }
    return $map
}

# One instruction with what the compiler chooses freely taken out: registers by kind only (r, v, o: their
# numbers and swizzles follow the packing), uniforms and textures by name, the operands of a commutative
# instruction in a fixed order. What is left: the operations, the literals, the inputs by name.
function Normalise([string]$line, [hashtable]$names) {
    $s = [regex]::Replace($line, 'cb(\d+)\[r\d+\.[xyzw] \+ (\d+)\](\.[xyzw]+)?', { param($m) $k = "cb$($m.Groups[1].Value)[+$($m.Groups[2].Value)]"; if ($names.ContainsKey($k)) { $names[$k] } else { $k } })
    $s = [regex]::Replace($s, 'cb(\d+)\[(\d+)\](\.[xyzw]+)?', { param($m) $k = "cb$($m.Groups[1].Value)[$($m.Groups[2].Value)]"; if ($names.ContainsKey($k)) { $names[$k] } else { $k } })
    $s = [regex]::Replace($s, '\b([ts]\d+)(\.[xyzw]+)?', { param($m) $k = $m.Groups[1].Value; if ($names.ContainsKey($k)) { $names[$k] } else { $k } })
    $s = [regex]::Replace($s, '\b([rvo])\d+(\.[xyzw]+)?', '$1')
    # A vector literal by the values in it (its lanes follow the packing): l(3, 0, 3, 0) = l(3).
    $s = [regex]::Replace($s, 'l\(([^)]*)\)', { param($m)
        $v = @($m.Groups[1].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' -and $_ -notmatch '^(0|0\.0+|0x00000000)$' } | Sort-Object -Unique)
        'l(' + ($v -join ',') + ')' })
    if ($s -match '^(\S+) (.*)$') {
        $op = $Matches[1]; $args = @([regex]::Split($Matches[2], ', (?![^()]*\))'))
        $base = $op -replace '_sat$', ''
        if ($args.Count -ge 3 -and $base -match '^(add|mul|max|min|eq|ne|and|or|dp2|dp3|dp4|mad)$') {
            $pair = @($args[1], $args[2]) | Sort-Object
            $args[1] = $pair[0]; $args[2] = $pair[1]
        }
        $s = $op + ' ' + ($args -join ', ')
    }
    return $s
}

function Dump([string]$file, [string]$name) {
    $dir = Join-Path $Work ($name -replace '/', '_')
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $ShaderDump -Name $name -AssetFile $file -OutDir $dir 2>$null
    $index = Get-ChildItem $dir -Filter *.index.txt | Select-Object -First 1
    if ($null -eq $index) { throw "Nothing dumped for $name from $file" }
    $list = @()
    foreach ($line in Get-Content $index.FullName) {
        $f = $line -split "`t"
        $keys = @(if ($f.Count -gt 3) { $f[3] -split ' ' | Where-Object { $_ -ne '' } })
        $asm = Get-ChildItem $dir -Filter ("*." + $f[0] + "." + $f[1] + ".asm") | Select-Object -First 1
        $text = @(Get-Content $asm.FullName | ForEach-Object { ($_ -replace "`0", '').TrimEnd() })
        $names = Names $text
        $code = @($text | Where-Object { $_ -ne '' -and $_ -notmatch '^(//|dcl_|vs_4|ps_4)' } | ForEach-Object { Normalise $_ $names })
        $list += [pscustomobject]@{ Kind = $f[1]; Keys = (($keys | Sort-Object) -join ' '); Code = $code; Index = $f[0] }
    }
    return $list
}

Add-Type -Language CSharp -TypeDefinition @'
public static class TreeBenchLcs
{
    // The longest common subsequence of two line lists, and the lines of each outside it.
    public static int Common(string[] a, string[] b)
    {
        int[,] t = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : System.Math.Max(t[i + 1, j], t[i, j + 1]);
        return t[0, 0];
    }

    public static string[] Diff(string[] a, string[] b)
    {
        int[,] t = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : System.Math.Max(t[i + 1, j], t[i, j + 1]);
        var o = new System.Collections.Generic.List<string>();
        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { x++; y++; }
            else if (t[x + 1, y] >= t[x, y + 1]) o.Add("ours  " + a[x++]);
            else o.Add("game  " + b[y++]);
        }
        while (x < a.Length) o.Add("ours  " + a[x++]);
        while (y < b.Length) o.Add("game  " + b[y++]);
        return o.ToArray();
    }
}
'@

$failed = 0

if ($Part -eq 'All' -or $Part -eq 'Asm') {
    Write-Host "Building the bench bundle..."
    $null = Run-Unity "TreeBench.Build"
    $bundle = Join-Path $project "TreeBench\Bundle\trees"
    foreach ($pair in @(@('Hidden/VW/GameTreesDefault', 'Custom/Trees/Default'), @('Hidden/VW/GameTreesGroup', 'Custom/Trees/Group'))) {
        $ours = Dump $bundle $pair[0]
        $game = Dump $GameAssets $pair[1]
        Write-Host ("== {0} against the game's {1}: {2} programs of ours" -f $pair[0], $pair[1], $ours.Count)
        foreach ($p in $ours | Sort-Object Kind, Keys) {
            # The game's passes carry INFOMODE_OFF too, its casters do not.
            $want = (@(($p.Keys -split ' ') + 'INFOMODE_OFF') | Where-Object { $_ -ne '' } | Sort-Object) -join ' '
            # Not the game's meta passes (lightmap baking; ours are built without, nometa).
            $candidates = @($game | Where-Object { $_.Kind -eq $p.Kind -and ($_.Keys -eq $want -or $_.Keys -eq $p.Keys) -and
                (($_.Code -join "`n") -notmatch 'unity_Meta(Vertex|Fragment)Control') })
            if ($candidates.Count -eq 0) {
                Write-Host ("  {0} [{1}]: NO game program with [{2}]" -f $p.Kind, $p.Keys, $want) -ForegroundColor Red
                $failed++
                continue
            }
            $best = $null; $bestCommon = -1
            $sorted = @($p.Code | Sort-Object) -join "`n"
            foreach ($c in $candidates) {
                $common = [TreeBenchLcs]::Common([string[]]$p.Code, [string[]]$c.Code)
                # A pass with the very same instructions wins over a closer order (the game has
                # several passes per keyword set: meta, deferred).
                if ((@($c.Code | Sort-Object) -join "`n") -eq $sorted) { $common += 100000 }
                if ($common -gt $bestCommon) { $best = $c; $bestCommon = $common }
            }
            if ($bestCommon -ge 100000) { $bestCommon -= 100000 }
            $same = $bestCommon -eq $p.Code.Count -and $bestCommon -eq $best.Code.Count
            # The same instructions in another order (the compiler's scheduling) count as the same.
            $reordered = -not $same -and ((@($p.Code | Sort-Object) -join "`n") -eq (@($best.Code | Sort-Object) -join "`n"))
            # Ours writing an output the game's does not (the surface shader's colour into the base pass,
            # whose pixel shader never reads it): every other instruction the same.
            $extra = $false
            if (-not $same -and -not $reordered) {
                $left = [System.Collections.Generic.List[string]]::new([string[]]$p.Code)
                $missing = 0
                foreach ($g in $best.Code) { if (-not $left.Remove($g)) { $missing++ } }
                $extra = $missing -eq 0 -and @($left | Where-Object { $_ -notmatch '^mov o, ' }).Count -eq 0
            }
            $verdict = if ($same) { "SAME" } elseif ($reordered) { "SAME (another order)" } elseif ($extra) { "SAME (+ an output its pixel shader never reads)" } else { "differs" }
            $same = $same -or $reordered -or $extra
            $line = "  {0} [{1}]: {2} lines, the game's #{3} {4} lines, {5} in common -- {6}" -f $p.Kind, $p.Keys, $p.Code.Count, $best.Index, $best.Code.Count, $bestCommon, $verdict
            if ($same) { Write-Host $line -ForegroundColor Green }
            else {
                Write-Host $line -ForegroundColor Yellow
                [TreeBenchLcs]::Diff([string[]]$p.Code, [string[]]$best.Code) | Select-Object -First $ShowLines | ForEach-Object { Write-Host ("      " + $_) }
                $failed++
            }
        }
    }
}

if ($Part -eq 'All' -or $Part -eq 'Render') {
    Write-Host "Rendering (the 5.6 editor with graphics)..."
    $log = Run-Unity "TreeBenchRun.Run" -graphics
    $lines = @(Select-String -Path $log -Pattern '^TREE BENCH' | ForEach-Object { $_.Line })
    $lines | ForEach-Object { Write-Host $_ }
    $fails = @($lines | Where-Object { $_ -match 'FAIL' })
    if ($lines.Count -eq 0) { Write-Host "No bench lines in $log" -ForegroundColor Red; $failed++ }
    $failed += $fails.Count

    # The GPU's wind points against the C# twin (Sky/TreeWind.PointAt), compiled from its source.
    Add-Type -Language CSharp -TypeDefinition (Get-Content -Raw (Join-Path $root "VolumetricClouds\Sky\TreeWind.cs"))
    foreach ($tsv in @((Join-Path $project "TreeBench\wind-points.tsv"), (Join-Path $project "TreeBench\wind-points-nogusts.tsv"))) {
    $g = New-Object VolumetricClouds.Sky.TreeWind+Globals
    $worst = 0.0; $sum = 0.0; $n = 0; $moved = 0.0; $edge = 0
    foreach ($row in Get-Content $tsv) {
        $f = $row -split "`t"
        if ($f[0] -eq '# globals') {
            $v = @($f[1..12] | ForEach-Object { [float]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
            $g.Lean = $v[0]; $g.Gusts = $v[1]; $g.Flutter = $v[2]; $g.DirX = $v[3]; $g.DirZ = $v[4]; $g.Clock = $v[5]
            $g.GustX = $v[6]; $g.GustZ = $v[7]; $g.InvCell = $v[8]; $g.SwayShare = $v[9]; $g.Period = $v[10]; $g.Across = $v[11]
            continue
        }
        if ($row.StartsWith('#')) { continue }
        $x = @($f | ForEach-Object { [float]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) })
        # A tree whose whole cycles sit on a rounding edge (within 0.01 of a half) may round the other
        # way on the GPU (its rsqrt is not the CPU's): a different rate, not a different law. Counted apart.
        $c = [math]::Min(1.4, [math]::Max(0.25, 2.5 / [math]::Sqrt([math]::Max([double]$x[8], 1.0)))) * $g.Period
        $cr = [math]::Max(1.0, [math]::Round($c, [MidpointRounding]::ToEven))
        $edges = @($c, ($cr * 0.73), ($cr * 3.0 * 1.37))
        if (@($edges | Where-Object { [math]::Abs(($_ - [math]::Floor($_)) - 0.5) -lt 0.01 }).Count -gt 0) { $edge++; continue }
        $ox = 0.0; $oy = 0.0; $oz = 0.0
        [VolumetricClouds.Sky.TreeWind]::PointAt($g, $x[0], $x[1], $x[2], $x[3], $x[4], $x[5], $x[6], $x[7], $x[8], $x[9], $x[10], $x[11], $x[12], $x[13], [ref]$ox, [ref]$oy, [ref]$oz)
        $e = [math]::Sqrt([double](($ox - $x[14]) * ($ox - $x[14]) + ($oy - $x[15]) * ($oy - $x[15]) + ($oz - $x[16]) * ($oz - $x[16])))
        $d = [math]::Sqrt([double](($ox - $x[0]) * ($ox - $x[0]) + ($oy - $x[1]) * ($oy - $x[1]) + ($oz - $x[2]) * ($oz - $x[2])))
        $worst = [math]::Max([double]$worst, $e); $sum += $e; $moved = [math]::Max([double]$moved, $d); $n++
    }
    $ok = $n -gt 0 -and $worst -lt 0.01
    $msg = "TREE BENCH {0} the wind's C# twin against the GPU ({5}): {1} points, mean {2:F2} mm, worst {3:F2} mm (moves up to {4:F2} m); {6} on a rounding edge set apart" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $n, (1000 * $sum / [math]::Max(1, $n)), (1000 * $worst), $moved, (Split-Path -Leaf $tsv), $edge
    Write-Host $msg -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    if (-not $ok) { $failed++ }
    }

    # The handover (~300 m): a near tree's crown and its billboard's top, each with the inputs its own
    # shader gives VWWindPoint (TreesDefault: the tree's place, its size x scale from _VWTreeSize = 2 x
    # the billboard's larger half-extent; TreesGroup: the foot under the billboard's centre, 2 x extent x
    # grow, grow 1 under 500 m), move alike over a minute of clocks and gusts.
    $half = 6.0; $scale = 1.3; $speed = 1.2
    $tree = @(1234.5, 41.0, -2210.25)
    $worstHand = 0.0; $most = 0.0
    $wind = [VolumetricClouds.Sky.TreeWind]::Compute(2.0, 1.0, 1.0, 1.0, 0.6, 0.4, 0.0)
    $motion = New-Object VolumetricClouds.Sky.TreeWind+Motion
    for ($step = 0; $step -lt 60; $step++) {
        $motion.Advance(1.0, 0.6, 0.8, $wind.GustSpeed)
        $gg = [VolumetricClouds.Sky.TreeWind]::GlobalsOf($wind, $motion, 0.6, 0.8)
        $H = 2.0 * $half * $scale
        # Near: the crown at the tree's height over its foot.
        $nx = 0.0; $ny = 0.0; $nz = 0.0
        [VolumetricClouds.Sky.TreeWind]::PointAt($gg, $tree[0], $tree[1] + $H, $tree[2], $tree[0], $tree[1], $tree[2], $tree[0], $tree[2], $H, $speed, 0.0, 0.0, 1.0, 0.0, [ref]$nx, [ref]$ny, [ref]$nz)
        # Billboard: centre at the foot + the scaled half-extent, the top a half-extent above it.
        $extent = $half * $scale
        $centreY = $tree[1] + $extent
        $footY = $centreY - $extent
        $bx = 0.0; $by = 0.0; $bz = 0.0
        [VolumetricClouds.Sky.TreeWind]::PointAt($gg, $tree[0], $centreY + $extent, $tree[2], $tree[0], $footY, $tree[2], $tree[0], $tree[2], 2.0 * $extent * 1.0, $speed, 0.0, 0.0, 1.0, 0.0, [ref]$bx, [ref]$by, [ref]$bz)
        $e = [math]::Sqrt([double](($nx - $bx) * ($nx - $bx) + ($ny - $by) * ($ny - $by) + ($nz - $bz) * ($nz - $bz)))
        $worstHand = [math]::Max([double]$worstHand, $e)
        $most = [math]::Max([double]$most, [math]::Sqrt([double](($nx - $tree[0]) * ($nx - $tree[0]) + ($nz - $tree[2]) * ($nz - $tree[2]))))
    }
    $ok = $worstHand -lt 0.001 -and $most -gt 0.05
    $msg = "TREE BENCH {0} the handover: a near tree's crown and its billboard's top over 60 s apart by at most {1:F2} mm (the crown moves up to {2:F2} m)" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), (1000 * $worstHand), $most
    Write-Host $msg -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    if (-not $ok) { $failed++ }
}

if ($failed -gt 0) { Write-Host "$failed check(s) not passed" -ForegroundColor Red; exit 1 }
Write-Host "All tree bench checks passed" -ForegroundColor Green
