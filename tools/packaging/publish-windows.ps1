[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repoRoot 'dist'
}

$project = Join-Path $repoRoot 'src\PhotoManager\PhotoManager.csproj'
$csproj = Get-Content -LiteralPath $project -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') {
    throw "Could not read Version from $project"
}
$version = $Matches[1]
$zipName = "PhotoManager-$version-$Runtime.zip"
$stage = Join-Path $OutputRoot "PhotoManager-$version-$Runtime"
$zipPath = Join-Path $OutputRoot $zipName

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host "Publishing self-contained $Runtime build $version"
dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $stage `
    -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$toolsSource = Join-Path $repoRoot 'tools\czkawka'
$toolsDestination = Join-Path $stage 'tools\czkawka'
New-Item -ItemType Directory -Path $toolsDestination -Force | Out-Null
Get-ChildItem -LiteralPath $toolsSource -Force |
    Where-Object { $_.Name -notin @('tests', 'bin', 'temp') -and $_.Name -notlike '*.local.json' } |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $toolsDestination $_.Name) -Recurse -Force
    }

$cliConfig = Join-Path $toolsDestination 'config.json'
& (Join-Path $toolsSource 'install.ps1') -ConfigPath $cliConfig -InstallDirectory (Join-Path $toolsDestination 'bin')
if ($LASTEXITCODE -ne 0) {
    throw "Czkawka CLI install failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-PhotoManager.ps1') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.bat') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.txt') -Destination $stage -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'GETTING_STARTED.md') -Destination $stage -Force

$required = @(
    'PhotoManager.exe',
    'tools\czkawka\scan.ps1',
    'tools\czkawka\repair-dates.ps1',
    'tools\czkawka\repair-orientation.ps1',
    'tools\czkawka\remediate.ps1',
    'tools\czkawka\config.json',
    'tools\czkawka\bin\czkawka_cli.exe',
    'Install.bat',
    'Install-PhotoManager.ps1',
    'README.txt',
    'GETTING_STARTED.md'
)
foreach ($relative in $required) {
    $path = Join-Path $stage $relative
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Packaged build is missing $relative"
    }
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "Packaged $zipPath"
[pscustomobject]@{
    Version = $version
    Stage = $stage
    ZipPath = $zipPath
    ZipBytes = (Get-Item -LiteralPath $zipPath).Length
}
