param(
    [Parameter(Position=0, Mandatory=$true)]
    [string]$DroppedPath
)

$ErrorActionPreference = 'Stop'

function Write-Title([string]$Text) {
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host " $Text" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
}

function Normalize-DroppedPath([string]$PathText) {
    $p = $PathText.Trim().Trim('"')
    if (-not (Test-Path -LiteralPath $p)) {
        throw "Dropped path does not exist: $p"
    }
    $item = Get-Item -LiteralPath $p
    if (-not $item.PSIsContainer) {
        return $item.Directory.FullName
    }
    return $item.FullName
}

function Test-GameRoot([string]$Dir) {
    return (
        (Test-Path -LiteralPath (Join-Path $Dir 'BepInEx\core\BepInEx.Core.dll')) -and
        (Test-Path -LiteralPath (Join-Path $Dir 'BepInEx\interop\Assembly-CSharp.dll'))
    )
}

function Find-GameRoot([string]$StartDir) {
    $dir = (Get-Item -LiteralPath $StartDir).FullName

    # If BepInEx itself was dropped, start at its parent.
    if ((Split-Path $dir -Leaf) -ieq 'BepInEx') {
        $candidate = Split-Path $dir -Parent
        if (Test-GameRoot $candidate) { return $candidate }
    }

    # Exact folder / parents first.
    $cursor = $dir
    for ($i = 0; $i -lt 5 -and $cursor; $i++) {
        if (Test-GameRoot $cursor) { return $cursor }
        $parent = Split-Path $cursor -Parent
        if (-not $parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }

    # Then inspect a few nearby child levels. This handles nested launch folders.
    $queue = New-Object System.Collections.Generic.Queue[object]
    $queue.Enqueue([pscustomobject]@{ Path = $dir; Depth = 0 })
    $visited = @{}
    while ($queue.Count -gt 0) {
        $node = $queue.Dequeue()
        if ($visited.ContainsKey($node.Path)) { continue }
        $visited[$node.Path] = $true

        if (Test-GameRoot $node.Path) { return $node.Path }
        if ($node.Depth -ge 3) { continue }

        try {
            Get-ChildItem -LiteralPath $node.Path -Directory -ErrorAction Stop | ForEach-Object {
                # Avoid walking giant unrelated trees.
                if ($_.Name -notmatch '^(\.git|src|source|backup|backups)$') {
                    $queue.Enqueue([pscustomobject]@{ Path = $_.FullName; Depth = $node.Depth + 1 })
                }
            }
        } catch { }
    }

    return $null
}

Write-Title 'More Players v2.20.6 - automatic build + replace'

$start = Normalize-DroppedPath $DroppedPath
$gameDir = Find-GameRoot $start
if (-not $gameDir) {
    throw @"
Could not find a valid BepInEx IL2CPP installation from:
  $start

I need both:
  BepInEx\core\BepInEx.Core.dll
  BepInEx\interop\Assembly-CSharp.dll

Drag the real game EXE, game root, or BepInEx folder onto the BAT file.
"@
}

Write-Host "Game root : $gameDir" -ForegroundColor Green
if (Get-Process -Name 'ApproximatelyUp' -ErrorAction SilentlyContinue) {
    throw "ApproximatelyUp.exe is running. Close the game before installing the mod."
}
$pluginsDir = Join-Path $gameDir 'BepInEx\plugins'
New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null

# Refuse while the dropped executable itself is running, where detectable.
try {
    $dropped = Get-Item -LiteralPath ($DroppedPath.Trim().Trim('"'))
    if (-not $dropped.PSIsContainer -and $dropped.Extension -ieq '.exe') {
        $procName = [IO.Path]::GetFileNameWithoutExtension($dropped.Name)
        if (Get-Process -Name $procName -ErrorAction SilentlyContinue) {
            throw "The game appears to be running ($procName.exe). Close it, then run the installer again."
        }
    }
} catch {
    if ($_.Exception.Message -like 'The game appears*') { throw }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $scriptRoot 'src\MorePlayersMod\MorePlayersMod.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    throw "Mod project is missing: $project"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw @"
.NET SDK was not found in PATH.
Install a .NET 6+ SDK, then run this same drag-and-drop installer again.
The mod targets net6.0.
"@
}

Write-Host "dotnet    : $($dotnet.Source)"
Write-Host "Project   : $project"

$destDll = Join-Path $pluginsDir 'MorePlayersMod.dll'
if (Test-Path -LiteralPath $destDll) {
    $backupDir = Join-Path $gameDir 'BepInEx\plugins-backups\MorePlayersMod Backups'
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    $stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
    $backup = Join-Path $backupDir "MorePlayersMod-$stamp.dll"
    Copy-Item -LiteralPath $destDll -Destination $backup -Force
    Write-Host "Backup    : $backup" -ForegroundColor DarkGray
}

Write-Title 'Building against your game files'
$propertyArg = '-p:GameDir=' + $gameDir
& dotnet build $project -c Release $propertyArg
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$builtDll = Join-Path $scriptRoot 'src\MorePlayersMod\bin\Release\net6.0\MorePlayersMod.dll'
if (-not (Test-Path -LiteralPath $builtDll)) {
    throw "Build reported success, but DLL was not found at: $builtDll"
}

# The csproj deploy target already copies it, but do one explicit replacement as well.
Copy-Item -LiteralPath $builtDll -Destination $destDll -Force

if (-not (Test-Path -LiteralPath $destDll)) {
    throw "Replacement DLL was not created in BepInEx\plugins."
}

$srcHash = (Get-FileHash -LiteralPath $builtDll -Algorithm SHA256).Hash
$dstHash = (Get-FileHash -LiteralPath $destDll -Algorithm SHA256).Hash
if ($srcHash -ne $dstHash) {
    throw "DLL verification failed: built DLL and installed DLL hashes differ."
}

$gameProcesses = Get-Process -Name 'ApproximatelyUp' -ErrorAction SilentlyContinue
if ($gameProcesses) {
    throw "ApproximatelyUp.exe is running. Close the game before invalidating the BepInEx chainloader cache."
}
$chainloaderCache = Join-Path $gameDir 'BepInEx\cache\chainloader_typeloader.dat'
if (Test-Path -LiteralPath $chainloaderCache) {
    Remove-Item -LiteralPath $chainloaderCache -Force
    Write-Host "Cache     : invalidated $chainloaderCache" -ForegroundColor DarkGray
}

Write-Title 'DONE'
Write-Host "Installed : $destDll" -ForegroundColor Green
Write-Host "Version   : 2.20.6" -ForegroundColor Green
Write-Host "Config    : preserved (nothing in BepInEx\config was replaced)" -ForegroundColor Green
Write-Host "SHA-256   : $dstHash" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Launch the game and test parts outside the original yellow build square." -ForegroundColor Yellow
exit 0
