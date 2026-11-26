<#
.SYNOPSIS
  Builds what's out of date and starts Dump Debugger.

.DESCRIPTION
  Does the "Run locally" steps from the README in one go:
    1. web/      npm install (only when package-lock.json changed) and npm run build (only when
                 the web sources are newer than web/dist)
    2. DumpGen   the sample generator behind Home's "Generate a sample"
    3. Host      the WinUI host, which also builds the x64 and x86 workers
    4. Starts the host's exe.

  With -Dev, the web build is skipped: a Vite dev server is started instead and the host is
  pointed at it (DUMPDEBUGGER_WEB_URL), so UI edits hot-reload. The dev server stops when you
  close the app.

.PARAMETER Dump
  A dump to open on start.

.PARAMETER Dev
  Hot reload: run the Vite dev server and point the host at it.

.PARAMETER NoBuild
  Skip every build step and start the last build.

.PARAMETER Configuration
  Debug (default) or Release.

.PARAMETER Cdp
  Expose the WebView's Chrome DevTools Protocol on this port (for web/scripts/shot.mjs).

.EXAMPLE
  ./run.ps1
.EXAMPLE
  ./run.ps1 fixtures/deadlock.dmp
.EXAMPLE
  ./run.ps1 -Dev
#>
param(
    [Parameter(Position = 0)]
    [string]$Dump,
    [switch]$Dev,
    [switch]$NoBuild,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [int]$Cdp
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$web = Join-Path $root 'web'
$devUrl = 'http://localhost:5173'

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

function Assert-Tool($name, $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { throw "$name isn't on PATH. $hint" }
}

function Get-NewestWrite($paths) {
    $newest = [datetime]::MinValue
    foreach ($p in $paths) {
        if (-not (Test-Path $p)) { continue }
        $item = Get-Item $p
        if ($item.PSIsContainer) {
            $file = Get-ChildItem $p -Recurse -File | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
            if ($file -and $file.LastWriteTimeUtc -gt $newest) { $newest = $file.LastWriteTimeUtc }
        }
        elseif ($item.LastWriteTimeUtc -gt $newest) { $newest = $item.LastWriteTimeUtc }
    }
    $newest
}

function Stop-Tree($process) {
    if ($process -and -not $process.HasExited) { taskkill /PID $process.Id /T /F 2>&1 | Out-Null }
}

# The host project picks the RID from the machine (win-x64 or win-arm64); match its Platform.
$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$platform = if ($arch -eq 'Arm64') { 'ARM64' } else { 'x64' }
$rid = if ($arch -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
$hostBin = Join-Path $root "src/DumpDebugger.Host/bin/$platform/$Configuration"

if (-not $NoBuild) {
    Assert-Tool 'dotnet' 'Install the .NET 10 SDK.'
    Assert-Tool 'npm' 'Install Node 20+.'

    # A running copy locks its exe and the bundled workers, so the build would fail.
    $running = Get-Process DumpDebugger.Host -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith((Resolve-Path (Join-Path $root 'src')).Path, 'OrdinalIgnoreCase') }
    if ($running) {
        Write-Host "Closing the running dev build (PID $($running.Id -join ', ')) so it can be rebuilt." -ForegroundColor Yellow
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }

    Step 'Web dependencies'
    $lock = Join-Path $web 'package-lock.json'
    $installed = Join-Path $web 'node_modules/.package-lock.json'
    if (-not (Test-Path $installed) -or (Get-Item $lock).LastWriteTimeUtc -gt (Get-Item $installed).LastWriteTimeUtc) {
        Push-Location $web
        try { npm install --no-audit --no-fund; if ($LASTEXITCODE) { throw 'npm install failed' } }
        finally { Pop-Location }
    }
    else { Write-Host 'Up to date.' }

    if (-not $Dev) {
        Step 'Web app'
        $distIndex = Join-Path $web 'dist/index.html'
        $sources = 'src', 'index.html', 'vite.config.ts', 'tsconfig.json', 'package-lock.json' | ForEach-Object { Join-Path $web $_ }
        if (-not (Test-Path $distIndex) -or (Get-NewestWrite $sources) -gt (Get-Item $distIndex).LastWriteTimeUtc) {
            Push-Location $web
            try { npm run build; if ($LASTEXITCODE) { throw 'Web build failed' } }
            finally { Pop-Location }
        }
        else { Write-Host 'Up to date.' }
    }

    Step 'Sample generator'
    dotnet build (Join-Path $root 'src/DumpDebugger.DumpGen') -c $Configuration -nologo -v q
    if ($LASTEXITCODE) { throw 'DumpGen build failed' }

    Step 'Host and workers'
    dotnet build (Join-Path $root 'src/DumpDebugger.Host') -c $Configuration -p:Platform=$platform -nologo -v q
    if ($LASTEXITCODE) { throw 'Host build failed' }
}

$exe = Get-ChildItem $hostBin -Recurse -Filter 'DumpDebugger.Host.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq $rid } |
    Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $exe) { throw "No DumpDebugger.Host.exe under $hostBin. Run without -NoBuild first." }

$hostArgs = @()
if ($Dump) {
    if (-not (Test-Path $Dump)) { throw "Dump not found: $Dump" }
    $hostArgs += '"' + (Resolve-Path $Dump).Path + '"'
}
if ($Cdp) { $hostArgs += '--cdp', $Cdp }

if (-not $Dev) {
    Step "Starting $($exe.FullName)"
    if ($hostArgs) { Start-Process $exe.FullName -ArgumentList $hostArgs } else { Start-Process $exe.FullName }
    return
}

Step "Vite dev server ($devUrl)"
$vite = Start-Process 'npm.cmd' -ArgumentList 'run', 'dev' -WorkingDirectory $web -PassThru -WindowStyle Minimized
try {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        if ($vite.HasExited) { throw 'The Vite dev server exited (is port 5173 already in use?).' }
        try { Invoke-WebRequest $devUrl -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { }
        if ((Get-Date) -gt $deadline) { throw "The Vite dev server didn't answer on $devUrl within 60 s." }
        Start-Sleep -Milliseconds 500
    }

    Step "Starting $($exe.FullName) (hot reload; close the app to stop the dev server)"
    $previous = $env:DUMPDEBUGGER_WEB_URL
    $env:DUMPDEBUGGER_WEB_URL = $devUrl
    try {
        $app = if ($hostArgs) { Start-Process $exe.FullName -ArgumentList $hostArgs -PassThru } else { Start-Process $exe.FullName -PassThru }
    }
    finally { $env:DUMPDEBUGGER_WEB_URL = $previous }
    $app.WaitForExit()
}
finally {
    Stop-Tree $vite
}
