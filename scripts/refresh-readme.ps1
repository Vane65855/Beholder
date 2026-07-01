<#
.SYNOPSIS
    Refreshes the README status badges from live sources so they can't drift:
    the release line comes from version.json, the passing-test count from an
    actual test run. Run it before a release or after adding/removing tests.

.EXAMPLE
    pwsh ./scripts/refresh-readme.ps1
#>
$ErrorActionPreference = "Stop"
$root   = Split-Path -Parent $PSScriptRoot
$readme = Join-Path $root "README.md"

# Release line (major.minor) — the badge shows this, not the auto-derived patch,
# which climbs every commit and would be perpetually stale in static markdown.
$prefix = (Get-Content (Join-Path $root "version.json") -Raw | ConvertFrom-Json).version

Write-Host "==> Building + running the test suite to count it" -ForegroundColor Cyan
dotnet build (Join-Path $root "Beholder.Tests\Beholder.Tests.csproj") -c Debug --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Test build failed (exit $LASTEXITCODE)." }

$exe = Join-Path $root "Beholder.Tests\bin\Debug\net10.0-windows10.0.17763.0\Beholder.Tests.exe"
$runnerOutput = & $exe | Out-String
$match = [regex]::Match($runnerOutput, 'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+)')
if (-not $match.Success) { throw "Could not parse the test summary from the runner output." }
$total = $match.Groups[1].Value
if ($match.Groups[2].Value -ne '0' -or $match.Groups[3].Value -ne '0') {
    throw "Tests are not green ($($match.Value)); not updating the badge."
}

$text = [System.IO.File]::ReadAllText($readme)
$text = [regex]::Replace($text, 'tests-\d+%20passing',             "tests-$total%20passing")
$text = [regex]::Replace($text, 'Tests:\s*\d+\s+passing',          "Tests: $total passing")
$text = [regex]::Replace($text, 'status-pre--release%20[0-9.]+',   "status-pre--release%20$prefix")
$text = [regex]::Replace($text, 'Status:\s*pre-release\s+[0-9.]+', "Status: pre-release $prefix")
[System.IO.File]::WriteAllText($readme, $text)

Write-Host "==> README badges updated: pre-release $prefix, $total tests passing." -ForegroundColor Green
