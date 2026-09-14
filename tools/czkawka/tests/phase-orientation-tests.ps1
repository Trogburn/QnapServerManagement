[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot '..\common-hash.ps1')

$root = Join-Path ([System.IO.Path]::GetTempPath()) "photo-orientation-tests-$([guid]::NewGuid().ToString('N'))"
New-Item -Path $root -ItemType Directory -Force | Out-Null
$scriptPath = Join-Path $PSScriptRoot '..\repair-orientation.ps1'

function New-JpegWithOrientation {
    param(
        [string]$LiteralPath,
        [int]$Orientation = 1,
        [int]$Width = 8,
        [int]$Height = 4
    )

    $bitmap = New-Object System.Drawing.Bitmap $Width, $Height
    try {
        for ($x = 0; $x -lt $Width; $x++) {
            for ($y = 0; $y -lt $Height; $y++) {
                $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $x * 20, $y * 40, 80))
            }
        }
        $bitmap.SetPixel(0, 0, [System.Drawing.Color]::Red)
        $bitmap.Save($LiteralPath, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    }
    finally {
        $bitmap.Dispose()
    }

    if ($Orientation -eq 1) {
        return
    }

    $image = [System.Drawing.Image]::FromFile($LiteralPath)
    try {
        $property = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([System.Drawing.Imaging.PropertyItem])
        $property.Id = 0x0112
        $property.Type = 3
        $property.Len = 2
        $property.Value = [BitConverter]::GetBytes([uint16]$Orientation)
        $image.SetPropertyItem($property)
        $tagged = "$LiteralPath.tagged"
        $image.Save($tagged, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    }
    finally {
        $image.Dispose()
    }

    Move-Item -LiteralPath $tagged -Destination $LiteralPath -Force
}

function Get-ImageSize {
    param([string]$LiteralPath)

    $image = [System.Drawing.Image]::FromFile($LiteralPath)
    try {
        [pscustomobject]@{ Width = $image.Width; Height = $image.Height }
    }
    finally {
        $image.Dispose()
    }
}

try {
    $upright = Join-Path $root 'upright.jpg'
    New-JpegWithOrientation -LiteralPath $upright -Orientation 1
    $sideways = Join-Path $root 'sideways.jpg'
    New-JpegWithOrientation -LiteralPath $sideways -Orientation 6
    $notes = Join-Path $root 'notes.txt'
    'not an image' | Set-Content -LiteralPath $notes -Encoding UTF8
    $beforeSideways = Get-Sha256Hex -LiteralPath $sideways
    $beforeUpright = Get-Sha256Hex -LiteralPath $upright
    $beforeSize = Get-ImageSize -LiteralPath $sideways

    $reviewPath = Join-Path $root 'review.json'
    & $scriptPath -Path $root -Recurse -OutputPath $reviewPath | Out-Null
    $report = Get-Content -LiteralPath $reviewPath -Raw | ConvertFrom-Json
    if (-not $report.dryRun) {
        throw 'Dry-run report must set dryRun=true.'
    }

    $sidewaysItem = @($report.items) | Where-Object { $_.path -eq ([IO.Path]::GetFullPath($sideways)) } | Select-Object -First 1
    $uprightItem = @($report.items) | Where-Object { $_.path -eq ([IO.Path]::GetFullPath($upright)) } | Select-Object -First 1
    $notesItem = @($report.items) | Where-Object { $_.path -eq ([IO.Path]::GetFullPath($notes)) } | Select-Object -First 1
    if ($sidewaysItem.status -ne 'Proposed' -or [int]$sidewaysItem.orientation -ne 6) {
        throw "Sideways JPEG was not proposed. status=$($sidewaysItem.status) orientation=$($sidewaysItem.orientation)"
    }
    if ([int]$sidewaysItem.proposedOrientation -ne 6) {
        throw "Sideways JPEG did not record proposedOrientation=6."
    }
    if ($uprightItem.status -ne 'AlreadyUpright') {
        throw "Normal JPEG was not AlreadyUpright. status=$($uprightItem.status)"
    }
    if ($notesItem.status -ne 'Unsupported') {
        throw "Text file was not Unsupported. status=$($notesItem.status)"
    }
    if ((Get-Sha256Hex -LiteralPath $sideways) -ne $beforeSideways) {
        throw 'Dry-run changed the sideways JPEG.'
    }

    $staleDecision = Join-Path $root 'stale-decision.json'
    @(
        [ordered]@{
            path = [IO.Path]::GetFullPath($sideways)
            action = 'approve'
        }
    ) | ConvertTo-Json | Set-Content -LiteralPath $staleDecision -Encoding UTF8
    $staleFile = Get-Item -LiteralPath $sideways
    $staleFile.LastWriteTimeUtc = $staleFile.LastWriteTimeUtc.AddMinutes(5)
    $applyFailed = $false
    try {
        & $scriptPath -ReviewPath $reviewPath -DecisionPath $staleDecision -Apply `
            -OutputPath (Join-Path $root 'stale-out.json') `
            -UndoManifestPath (Join-Path $root 'stale-undo.jsonl') `
            -VerificationReportPath (Join-Path $root 'stale-verify.json') `
            -BackupDirectory (Join-Path $root 'stale-backups') | Out-Null
    }
    catch {
        $applyFailed = $true
        if ($_.Exception.Message -notlike '*stale*') {
            throw "Stale apply failed for the wrong reason: $($_.Exception.Message)"
        }
    }
    if (-not $applyFailed) {
        throw 'Stale apply was not refused.'
    }

    New-JpegWithOrientation -LiteralPath $sideways -Orientation 6
    $beforeSideways = Get-Sha256Hex -LiteralPath $sideways
    $reviewPath = Join-Path $root 'review-apply.json'
    & $scriptPath -Path $sideways -OutputPath $reviewPath | Out-Null
    $decisionPath = Join-Path $root 'decision.json'
    @(
        [ordered]@{
            path = [IO.Path]::GetFullPath($sideways)
            action = 'approve'
        }
    ) | ConvertTo-Json | Set-Content -LiteralPath $decisionPath -Encoding UTF8
    $manifest = Join-Path $root 'undo.jsonl'
    $verifyPath = Join-Path $root 'verify.json'
    $backupDir = Join-Path $root 'backups'
    & $scriptPath -ReviewPath $reviewPath -DecisionPath $decisionPath -Apply `
        -OutputPath (Join-Path $root 'applied-review.json') `
        -UndoManifestPath $manifest `
        -VerificationReportPath $verifyPath `
        -BackupDirectory $backupDir | Out-Null

    $verify = Get-Content -LiteralPath $verifyPath -Raw | ConvertFrom-Json
    if (-not $verify.passed -or [int]$verify.appliedCount -ne 1) {
        throw "Apply verification report was not successful. passed=$($verify.passed) count=$($verify.appliedCount)"
    }
    $afterHash = Get-Sha256Hex -LiteralPath $sideways
    if ($afterHash -eq $beforeSideways) {
        throw 'Apply did not change JPEG bytes.'
    }
    $afterSize = Get-ImageSize -LiteralPath $sideways
    if ($afterSize.Width -ne $beforeSize.Height -or $afterSize.Height -ne $beforeSize.Width) {
        throw "Apply did not swap dimensions for orientation 6. before=$($beforeSize.Width)x$($beforeSize.Height) after=$($afterSize.Width)x$($afterSize.Height)"
    }
    if ((Get-Sha256Hex -LiteralPath $upright) -ne $beforeUpright) {
        throw 'Apply changed an unapproved upright JPEG.'
    }

    $entry = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    if (-not (Test-Path -LiteralPath $entry.backupPath)) {
        throw 'Apply did not keep a backup copy.'
    }

    & $scriptPath -Undo -UndoManifestPath $manifest | Out-Null
    if ((Get-Sha256Hex -LiteralPath $sideways) -ne $beforeSideways) {
        throw 'Undo did not restore the original JPEG bytes.'
    }
    $restoredSize = Get-ImageSize -LiteralPath $sideways
    if ($restoredSize.Width -ne $beforeSize.Width -or $restoredSize.Height -ne $beforeSize.Height) {
        throw 'Undo did not restore the original dimensions.'
    }

    $contentSideways = Join-Path $root 'content-sideways.jpg'
    New-JpegWithOrientation -LiteralPath $contentSideways -Orientation 1
    $beforeContent = Get-Sha256Hex -LiteralPath $contentSideways
    $beforeContentSize = Get-ImageSize -LiteralPath $contentSideways
    $contentFile = Get-Item -LiteralPath $contentSideways
    $contentReviewPath = Join-Path $root 'content-review.json'
    @{
        schemaVersion = 1
        generatedAtUtc = [datetime]::UtcNow.ToString('o')
        dryRun = $true
        policy = 'BakeExifOrientation'
        items = @(
            [ordered]@{
                path = [IO.Path]::GetFullPath($contentSideways)
                size = [long]$contentFile.Length
                currentCreationTimeUtc = $contentFile.CreationTimeUtc.ToString('o')
                currentLastWriteTimeUtc = $contentFile.LastWriteTimeUtc.ToString('o')
                width = [int]$beforeContentSize.Width
                height = [int]$beforeContentSize.Height
                orientation = 1
                orientationLabel = 'Normal'
                proposedRotation = 'Rotate 90 clockwise so the stored pixels are upright.'
                proposedOrientation = 6
                status = 'Proposed'
                reason = 'Content detector fixture.'
                confidence = 'Medium'
                decodeStatus = 'renderable'
            }
        )
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $contentReviewPath -Encoding UTF8
    $contentDecision = Join-Path $root 'content-decision.json'
    @(
        [ordered]@{
            path = [IO.Path]::GetFullPath($contentSideways)
            action = 'approve'
        }
    ) | ConvertTo-Json | Set-Content -LiteralPath $contentDecision -Encoding UTF8
    & $scriptPath -ReviewPath $contentReviewPath -DecisionPath $contentDecision -Apply `
        -OutputPath (Join-Path $root 'content-applied.json') `
        -UndoManifestPath (Join-Path $root 'content-undo.jsonl') `
        -VerificationReportPath (Join-Path $root 'content-verify.json') `
        -BackupDirectory (Join-Path $root 'content-backups') | Out-Null
    $afterContentSize = Get-ImageSize -LiteralPath $contentSideways
    if ($afterContentSize.Width -ne $beforeContentSize.Height -or $afterContentSize.Height -ne $beforeContentSize.Width) {
        throw "Content proposedOrientation apply did not swap dimensions. before=$($beforeContentSize.Width)x$($beforeContentSize.Height) after=$($afterContentSize.Width)x$($afterContentSize.Height)"
    }
    if ((Get-Sha256Hex -LiteralPath $contentSideways) -eq $beforeContent) {
        throw 'Content proposedOrientation apply did not change JPEG bytes.'
    }

    Write-Host 'Orientation repair tests passed.'
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
