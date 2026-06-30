<#
.SYNOPSIS
    Builds the Beholder NMT MSI: publishes the daemon + UI self-contained for
    win-x64, strips debug symbols, then harvests both into a single installer.

.DESCRIPTION
    Self-contained means the MSI carries .NET 10 and needs no runtime on the
    target machine. The two apps publish into their own staging dirs and land in
    C:\Program Files\Beholder\{Daemon,UI} so their bundled runtime DLLs don't
    collide. Requires the WiX v5 local tool (dotnet tool restore) and is
    Windows-only (WiX builds MSIs on Windows).

.EXAMPLE
    pwsh ./build-installer.ps1
    Builds the MSI at the version in version.json (the single source of truth).

.EXAMPLE
    pwsh ./build-installer.ps1 -Version 0.2.0-rc1
    Overrides the version for a one-off build without editing version.json.
#>
param(
    [string]$Version = (Get-Content (Join-Path $PSScriptRoot "version.json") -Raw | ConvertFrom-Json).version,
    [string]$Config  = "Release"
)

$ErrorActionPreference = "Stop"
$root        = $PSScriptRoot
$daemonStage = Join-Path $root "publish-daemon"
$uiStage     = Join-Path $root "publish-ui"
$wixproj     = Join-Path $root "Beholder.Installer\Beholder.Installer.wixproj"

function Invoke-Checked {
    param([string]$What, [scriptblock]$Action)
    Write-Host "==> $What" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)." }
}

# The stale staging dirs are framework-dependent — harvesting them would ship a
# broken MSI. Always start clean.
Write-Host "==> Cleaning staging" -ForegroundColor Cyan
Remove-Item -Recurse -Force $daemonStage, $uiStage -ErrorAction SilentlyContinue

$publishArgs = @(
    "-c", $Config, "-r", "win-x64", "--self-contained", "true",
    "-p:PublishTrimmed=false", "-p:DebugType=none", "-p:DebugSymbols=false",
    "-p:Version=$Version"
)

Invoke-Checked "Publishing daemon (self-contained win-x64)" {
    dotnet publish (Join-Path $root "Beholder.Daemon\Beholder.Daemon.csproj") @publishArgs -o $daemonStage
}
Invoke-Checked "Publishing UI (self-contained win-x64)" {
    dotnet publish (Join-Path $root "Beholder.Ui\Beholder.Ui.csproj") @publishArgs -o $uiStage
}

# DebugType=none drops the managed PDBs, but the native Skia/HarfBuzz PDBs ship
# with their runtime packages (~100 MB) — strip them so they don't bloat the MSI.
Write-Host "==> Stripping *.pdb from staging" -ForegroundColor Cyan
Get-ChildItem -Path $daemonStage, $uiStage -Recurse -Filter *.pdb | Remove-Item -Force

Invoke-Checked "Building MSI" {
    dotnet build $wixproj -c $Config -p:Version=$Version
}

$msi = Join-Path $root "Beholder.Installer\bin\$Config\BeholderNMT-$Version-win-x64.msi"
$sizeMb = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "==> Done: $msi ($sizeMb MB)" -ForegroundColor Green
