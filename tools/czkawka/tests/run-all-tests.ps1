[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$testRoot = $PSScriptRoot
$tests = @(
    'local-config-tests.ps1',
    'phase1-tests.ps1',
    'phase2-smoke.ps1',
    'phase2-tests.ps1',
    'phase3-smoke.ps1',
    'phase3-tests.ps1',
    'phase-date-reviewer-contract-tests.ps1',
    'phase4-tests.ps1',
    'phase5-tests.ps1',
    'phase6-tests.ps1',
    'phase7-tests.ps1',
    'phase8-tests.ps1',
    'phase-orientation-tests.ps1'
)

foreach ($test in $tests) {
    $path = Join-Path $testRoot $test
    Write-Host "Running $test"
    $global:LASTEXITCODE = 0
    & $path
    if (-not $?) {
        throw "$test failed."
    }
    if ($LASTEXITCODE -ne 0) {
        throw "$test failed with exit code $LASTEXITCODE."
    }
}

Write-Host 'All phase validation tests passed.'
