[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string[]]$Path,

    [Parameter()]
    [string]$OutputPath = '.\reports\rotate\orientation-review.json',

    [Parameter()]
    [switch]$Recurse,

    [Parameter()]
    [switch]$Apply,

    [Parameter()]
    [switch]$Undo,

    [Parameter()]
    [string]$ReviewPath,

    [Parameter()]
    [string]$DecisionPath,

    [Parameter()]
    [string]$UndoManifestPath = '.\reports\rotate\orientation-undo.jsonl',

    [Parameter()]
    [string]$VerificationReportPath = '.\reports\rotate\orientation-verification.json',

    [Parameter()]
    [string]$BackupDirectory = '.\reports\rotate\backups'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common-hash.ps1')
Add-Type -AssemblyName System.Drawing

$script:SupportedExtensions = @('.jpg', '.jpeg', '.tif', '.tiff')
$script:OrientationTagId = 0x0112

function Read-JsonlEntries {
    param([string]$LiteralPath)

    $entries = @()
    $buffer = New-Object System.Text.StringBuilder
    foreach ($line in Get-Content -LiteralPath $LiteralPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        [void]$buffer.AppendLine($line)
        try {
            $parsed = $buffer.ToString() | ConvertFrom-Json -ErrorAction Stop
            if ($null -ne $parsed) {
                $entries += $parsed
                [void]$buffer.Clear()
            }
        }
        catch {
            # Incomplete JSON object; keep buffering until the closing brace arrives.
        }
    }

    if ($buffer.Length -gt 0) {
        throw "Undo manifest contained incomplete JSON: $LiteralPath"
    }

    return @($entries)
}

function Get-OrientationLabel {
    param([object]$Value)

    switch ([int]$Value) {
        1 { 'Normal' }
        2 { 'Mirror horizontal' }
        3 { 'Rotate 180' }
        4 { 'Mirror vertical' }
        5 { 'Mirror horizontal and rotate 270 CW' }
        6 { 'Rotate 90 CW' }
        7 { 'Mirror horizontal and rotate 90 CW' }
        8 { 'Rotate 270 CW' }
        default { "Unknown ($Value)" }
    }
}

function Get-ProposedRotation {
    param([int]$Orientation)

    switch ($Orientation) {
        2 { 'Flip horizontally so the stored pixels are upright.' }
        3 { 'Rotate 180 so the stored pixels are upright.' }
        4 { 'Flip vertically so the stored pixels are upright.' }
        5 { 'Transpose (mirror and rotate) so the stored pixels are upright.' }
        6 { 'Rotate 90 clockwise so the stored pixels are upright.' }
        7 { 'Transverse (mirror and rotate) so the stored pixels are upright.' }
        8 { 'Rotate 90 counter-clockwise so the stored pixels are upright.' }
        default { $null }
    }
}

function Get-RotateFlipType {
    param([int]$Orientation)

    switch ($Orientation) {
        2 { [Drawing.RotateFlipType]::RotateNoneFlipX }
        3 { [Drawing.RotateFlipType]::Rotate180FlipNone }
        4 { [Drawing.RotateFlipType]::Rotate180FlipX }
        5 { [Drawing.RotateFlipType]::Rotate90FlipX }
        6 { [Drawing.RotateFlipType]::Rotate90FlipNone }
        7 { [Drawing.RotateFlipType]::Rotate270FlipX }
        8 { [Drawing.RotateFlipType]::Rotate270FlipNone }
        default { [Drawing.RotateFlipType]::RotateNoneFlipNone }
    }
}

function Open-ImageFromPath {
    param([string]$LiteralPath)

    $bytes = [IO.File]::ReadAllBytes($LiteralPath)
    $stream = New-Object IO.MemoryStream
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Position = 0
    $image = [Drawing.Image]::FromStream($stream)
    [pscustomobject]@{
        Image = $image
        Stream = $stream
    }
}

function Get-ExifOrientation {
    param($Image)

    $item = @($Image.PropertyItems) | Where-Object { $_.Id -eq $script:OrientationTagId } | Select-Object -First 1
    if ($null -eq $item -or $null -eq $item.Value -or $item.Value.Length -lt 2) {
        return $null
    }

    return [int][BitConverter]::ToUInt16($item.Value, 0)
}

function Set-ExifOrientationNormal {
    param($Image)

    $property = [Runtime.Serialization.FormatterServices]::GetUninitializedObject([Drawing.Imaging.PropertyItem])
    $property.Id = $script:OrientationTagId
    $property.Type = 3
    $property.Len = 2
    $property.Value = [BitConverter]::GetBytes([uint16]1)
    $Image.SetPropertyItem($property)
}

function Save-ImageReplacing {
    param(
        $Image,
        [string]$LiteralPath
    )

    $extension = [IO.Path]::GetExtension($LiteralPath).ToLowerInvariant()
    $tempPath = "$LiteralPath.rotate-tmp"
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force
    }

    if ($extension -in @('.jpg', '.jpeg')) {
        $codec = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
            Where-Object { $_.MimeType -eq 'image/jpeg' } |
            Select-Object -First 1
        $encoderParams = New-Object Drawing.Imaging.EncoderParameters 1
        $encoderParams.Param[0] = New-Object Drawing.Imaging.EncoderParameter ([Drawing.Imaging.Encoder]::Quality, [long]95)
        $Image.Save($tempPath, $codec, $encoderParams)
    }
    elseif ($extension -in @('.tif', '.tiff')) {
        $Image.Save($tempPath, [Drawing.Imaging.ImageFormat]::Tiff)
    }
    else {
        throw "Unsupported image type for apply: $LiteralPath"
    }

    Move-Item -LiteralPath $tempPath -Destination $LiteralPath -Force
}

function Get-FileOrientationReview {
    param([System.IO.FileInfo]$File)

    $extension = $File.Extension.ToLowerInvariant()
    if ($extension -notin $script:SupportedExtensions) {
        return [ordered]@{
            path = $File.FullName
            size = [long]$File.Length
            currentCreationTimeUtc = $File.CreationTimeUtc.ToString('o')
            currentLastWriteTimeUtc = $File.LastWriteTimeUtc.ToString('o')
            width = $null
            height = $null
            orientation = $null
            orientationLabel = $null
            proposedRotation = $null
            proposedOrientation = $null
            status = 'Unsupported'
            reason = 'Orientation repair currently supports JPEG and TIFF only.'
            confidence = 'None'
            decodeStatus = 'not-applicable'
        }
    }

    $opened = $null
    try {
        $opened = Open-ImageFromPath -LiteralPath $File.FullName
        $orientation = Get-ExifOrientation -Image $opened.Image
        $label = if ($null -eq $orientation) { $null } else { Get-OrientationLabel -Value $orientation }
        if ($null -eq $orientation -or $orientation -eq 1) {
            $status = 'AlreadyUpright'
            $reason = if ($null -eq $orientation) {
                'No EXIF Orientation tag; stored pixels are used as-is.'
            }
            else {
                'EXIF Orientation is Normal; stored pixels are already upright.'
            }
            $proposed = $null
            $proposedOrientation = $null
            $confidence = 'None'
        }
        elseif ($orientation -ge 2 -and $orientation -le 8) {
            $status = 'Proposed'
            $reason = "EXIF Orientation $orientation ($label) means viewers that ignore the tag show the photo sideways or upside down."
            $proposed = Get-ProposedRotation -Orientation $orientation
            $proposedOrientation = $orientation
            $confidence = 'High'
        }
        else {
            $status = 'InvalidOrientation'
            $reason = "EXIF Orientation $orientation is not a supported rotation."
            $proposed = $null
            $proposedOrientation = $null
            $confidence = 'None'
        }

        return [ordered]@{
            path = $File.FullName
            size = [long]$File.Length
            currentCreationTimeUtc = $File.CreationTimeUtc.ToString('o')
            currentLastWriteTimeUtc = $File.LastWriteTimeUtc.ToString('o')
            width = [int]$opened.Image.Width
            height = [int]$opened.Image.Height
            orientation = $orientation
            orientationLabel = $label
            proposedRotation = $proposed
            proposedOrientation = $proposedOrientation
            status = $status
            reason = $reason
            confidence = $confidence
            decodeStatus = 'renderable'
        }
    }
    catch {
        return [ordered]@{
            path = $File.FullName
            size = [long]$File.Length
            currentCreationTimeUtc = $File.CreationTimeUtc.ToString('o')
            currentLastWriteTimeUtc = $File.LastWriteTimeUtc.ToString('o')
            width = $null
            height = $null
            orientation = $null
            orientationLabel = $null
            proposedRotation = $null
            proposedOrientation = $null
            status = 'Unreadable'
            reason = $_.Exception.Message
            confidence = 'None'
            decodeStatus = 'unrenderable'
        }
    }
    finally {
        if ($null -ne $opened) {
            $opened.Image.Dispose()
            $opened.Stream.Dispose()
        }
    }
}

function Get-InputFiles {
    param([string[]]$Roots)

    foreach ($root in $Roots) {
        if (-not (Test-Path -LiteralPath $root)) {
            throw "Input path not found: $root"
        }
        $item = Get-Item -LiteralPath $root -Force
        if ($item -is [System.IO.FileInfo]) {
            $item
        }
        elseif ($Recurse) {
            Get-ChildItem -LiteralPath $root -File -Recurse -Force
        }
        else {
            Get-ChildItem -LiteralPath $root -File -Force
        }
    }
}

function Test-FileTimeMatch {
    param(
        [datetime]$Actual,
        [object]$Expected
    )

    $expectedDate = if ($Expected -is [datetime]) {
        ([datetime]$Expected).ToUniversalTime()
    }
    else {
        [datetime]::Parse([string]$Expected).ToUniversalTime()
    }
    return [math]::Abs(($Actual.ToUniversalTime() - $expectedDate).TotalSeconds) -le 1
}

function Get-ApplyOrientation {
    param($Review)

    $property = $Review.PSObject.Properties['proposedOrientation']
    if ($null -ne $property -and $null -ne $property.Value -and [string]$property.Value -ne '') {
        return [int]$property.Value
    }

    return [int]$Review.orientation
}

function Invoke-OrientationApply {
    param(
        [object]$Review,
        [string]$BackupRoot
    )

    $file = Get-Item -LiteralPath $Review.path -Force
    if ($file.Length -ne [long]$Review.size -or -not (Test-FileTimeMatch -Actual $file.LastWriteTimeUtc -Expected $Review.currentLastWriteTimeUtc)) {
        throw "Apply refused; report is stale: $($Review.path)"
    }

    $orientation = Get-ApplyOrientation -Review $Review
    if ($orientation -lt 2 -or $orientation -gt 8) {
        throw "Apply refused; orientation is not a rotate candidate: $($Review.path)"
    }

    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    $backupName = '{0}-{1}' -f [guid]::NewGuid().ToString('N'), $file.Name
    $backupPath = Join-Path $BackupRoot $backupName
    Copy-Item -LiteralPath $file.FullName -Destination $backupPath -Force
    $beforeSha = Get-Sha256Hex -LiteralPath $file.FullName
    $backupSha = Get-Sha256Hex -LiteralPath $backupPath
    if ($beforeSha -ne $backupSha) {
        throw "Apply refused; backup hash mismatch: $($Review.path)"
    }

    $creationUtc = $file.CreationTimeUtc
    $opened = Open-ImageFromPath -LiteralPath $file.FullName
    try {
        $opened.Image.RotateFlip((Get-RotateFlipType -Orientation $orientation))
        Set-ExifOrientationNormal -Image $opened.Image
        Save-ImageReplacing -Image $opened.Image -LiteralPath $file.FullName
    }
    finally {
        $opened.Image.Dispose()
        $opened.Stream.Dispose()
    }

    $updated = Get-Item -LiteralPath $file.FullName -Force
    $updated.CreationTimeUtc = $creationUtc
    $afterSha = Get-Sha256Hex -LiteralPath $updated.FullName
    if ($afterSha -eq $beforeSha) {
        throw "Apply verification failed; file content did not change: $($Review.path)"
    }

    $verifyOpened = Open-ImageFromPath -LiteralPath $updated.FullName
    try {
        $afterOrientation = Get-ExifOrientation -Image $verifyOpened.Image
        if ($null -ne $afterOrientation -and $afterOrientation -ne 1) {
            throw "Apply verification failed; orientation was not Normal: $($Review.path)"
        }
        $afterWidth = [int]$verifyOpened.Image.Width
        $afterHeight = [int]$verifyOpened.Image.Height
    }
    finally {
        $verifyOpened.Image.Dispose()
        $verifyOpened.Stream.Dispose()
    }

    [ordered]@{
        path = $file.FullName
        backupPath = $backupPath
        orientationBefore = $orientation
        orientationAfter = 1
        beforeSha256 = $beforeSha
        afterSha256 = $afterSha
        beforeSize = [long]$Review.size
        afterSize = [long]$updated.Length
        beforeWidth = [int]$Review.width
        beforeHeight = [int]$Review.height
        afterWidth = $afterWidth
        afterHeight = $afterHeight
        beforeCreationTimeUtc = $creationUtc.ToString('o')
        beforeLastWriteTimeUtc = ([datetime]$Review.currentLastWriteTimeUtc).ToUniversalTime().ToString('o')
        afterCreationTimeUtc = $updated.CreationTimeUtc.ToString('o')
        afterLastWriteTimeUtc = $updated.LastWriteTimeUtc.ToString('o')
        appliedAtUtc = [datetime]::UtcNow.ToString('o')
        verificationPassed = $true
    }
}

function Write-JsonDocument {
    param(
        [object]$Document,
        [string]$LiteralPath
    )

    $directory = [IO.Path]::GetDirectoryName($LiteralPath)
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    ($Document | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $LiteralPath -Encoding UTF8
}

$resolvedManifestPath = [System.IO.Path]::GetFullPath($UndoManifestPath)
$resolvedBackupDirectory = [System.IO.Path]::GetFullPath($BackupDirectory)

if ($Undo) {
    if (-not (Test-Path -LiteralPath $resolvedManifestPath)) {
        throw "Undo manifest not found: $UndoManifestPath"
    }

    $manifestEntries = @(Read-JsonlEntries -LiteralPath $resolvedManifestPath)
    foreach ($entry in $manifestEntries) {
        if (-not (Test-Path -LiteralPath $entry.path)) {
            throw "Undo refused; file is missing: $($entry.path)"
        }
        if (-not (Test-Path -LiteralPath $entry.backupPath)) {
            throw "Undo refused; backup is missing: $($entry.backupPath)"
        }

        $currentSha = Get-Sha256Hex -LiteralPath $entry.path
        if ($currentSha -ne [string]$entry.afterSha256) {
            throw "Undo refused; file changed since rotate: $($entry.path)"
        }

        Copy-Item -LiteralPath $entry.backupPath -Destination $entry.path -Force
        $restored = Get-Item -LiteralPath $entry.path -Force
        $restored.CreationTimeUtc = [datetime]$entry.beforeCreationTimeUtc
        $restored.LastWriteTimeUtc = [datetime]$entry.beforeLastWriteTimeUtc
        $restoredSha = Get-Sha256Hex -LiteralPath $entry.path
        if ($restoredSha -ne [string]$entry.beforeSha256) {
            throw "Undo verification failed; original bytes were not restored: $($entry.path)"
        }
    }

    Write-Host "Undo completed for $($manifestEntries.Count) file(s)."
    return
}

$inputPathCount = if ($null -eq $Path) { 0 } else { $Path.Count }
if ($inputPathCount -eq 0 -and [string]::IsNullOrWhiteSpace($ReviewPath)) {
    throw 'At least one input path or -ReviewPath is required unless -Undo is specified.'
}

$reviews = @()
if (-not [string]::IsNullOrWhiteSpace($ReviewPath)) {
    if (-not (Test-Path -LiteralPath $ReviewPath)) {
        throw "Review report not found: $ReviewPath"
    }
    $reviewDocument = Get-Content -LiteralPath $ReviewPath -Raw | ConvertFrom-Json
    $reviews = @($reviewDocument.items)
}
else {
    $files = @(Get-InputFiles -Roots $Path | Sort-Object FullName -Unique)
    foreach ($file in $files) {
        $reviews += Get-FileOrientationReview -File $file
    }
}

$decisionByPath = @{}
if ($Apply) {
    if ([string]::IsNullOrWhiteSpace($DecisionPath) -or -not (Test-Path -LiteralPath $DecisionPath)) {
        throw 'Apply requires a decision file. Scan stays dry-run until you approve files.'
    }

    $decisions = @(Get-Content -LiteralPath $DecisionPath -Raw | ConvertFrom-Json)
    foreach ($decision in $decisions) {
        $decisionByPath[[string]$decision.path] = [string]$decision.action
    }
}

$applied = @()
$verificationItems = @()
if ($Apply) {
    $manifestDirectory = [IO.Path]::GetDirectoryName($resolvedManifestPath)
    if (-not [string]::IsNullOrWhiteSpace($manifestDirectory)) {
        New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
    }

    foreach ($review in $reviews) {
        $action = $decisionByPath[[string]$review.path]
        if ([string]::IsNullOrWhiteSpace($action) -or $action -notin @('approve', 'Approve')) {
            $verificationItems += [ordered]@{
                path = $review.path
                action = if ([string]::IsNullOrWhiteSpace($action)) { 'skip' } else { $action }
                passed = $true
                changed = $false
            }
            continue
        }

        $entry = Invoke-OrientationApply -Review $review -BackupRoot $resolvedBackupDirectory
        $applied += $entry
        $line = ($entry | ConvertTo-Json -Compress -Depth 6)
        Add-Content -LiteralPath $resolvedManifestPath -Value $line -Encoding UTF8
        $verificationItems += [ordered]@{
            path = $entry.path
            action = 'approve'
            passed = $true
            changed = $true
            backupPath = $entry.backupPath
        }
    }
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
Write-JsonDocument -LiteralPath $resolvedOutputPath -Document ([ordered]@{
    schemaVersion = 1
    generatedAtUtc = [datetime]::UtcNow.ToString('o')
    dryRun = -not $Apply
    policy = 'BakeExifOrientation'
    items = @($reviews)
})

if ($Apply) {
    $passed = -not (@($verificationItems) | Where-Object { -not $_.passed })
    Write-JsonDocument -LiteralPath ([System.IO.Path]::GetFullPath($VerificationReportPath)) -Document ([ordered]@{
        schemaVersion = 1
        generatedAtUtc = [datetime]::UtcNow.ToString('o')
        passed = [bool]$passed
        appliedCount = @($applied).Count
        items = @($verificationItems)
    })
    if (-not $passed) {
        throw 'Orientation apply completed with a failed verification report.'
    }
    Write-Host "Applied $(@($applied).Count) orientation change(s)."
}
else {
    Write-Host "Orientation dry-run wrote $resolvedOutputPath"
}
