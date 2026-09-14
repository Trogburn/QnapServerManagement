using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoManager.Models;

namespace PhotoManager.Services;

public sealed class OrientationRepairService(PathPolicy pathPolicy)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions JsonlOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly PathPolicy _pathPolicy =
        pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));

    public async Task<(string ReportPath, OrientationReviewReport Report)> ScanAsync(
        string scanRoot,
        string artifactRoot,
        CancellationToken cancellationToken = default)
    {
        PathPolicy.ValidateScanRoot(scanRoot);
        var reportPath = ResolveArtifact(artifactRoot, "rotate", "orientation-review.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        await RunPowerShellAsync(
            [
                "-File", FindScript(),
                "-Path", scanRoot,
                "-OutputPath", reportPath,
                "-Recurse"
            ],
            cancellationToken);

        var report = await ReadReportAsync(reportPath, cancellationToken);
        await OrientationContentAnalyzer.EnrichAlreadyUprightItemsAsync(report.Items, cancellationToken);
        await SaveReportAsync(reportPath, report, cancellationToken);
        return (reportPath, report);
    }

    public async Task SaveReportAsync(
        string reportPath,
        OrientationReviewReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(
            reportPath,
            JsonSerializer.Serialize(report, JsonOptions),
            Encoding.UTF8,
            cancellationToken);
    }

    public async Task<OrientationSnapshot> CreateSnapshotAsync(
        string reviewPath,
        OrientationReviewReport report,
        string artifactRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var items = new List<OrientationSnapshotItem>();
        foreach (var item in report.Items.Where(IsApplyCandidate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = new FileInfo(item.Path);
            if (!file.Exists)
            {
                throw new IOException($"Snapshot refused; file is missing: {item.Path}");
            }

            if (file.Length != item.Size
                || !NearlyEqual(file.LastWriteTimeUtc, ParseUtc(item.CurrentLastWriteTimeUtc)))
            {
                throw new IOException($"Snapshot refused; report is stale: {item.Path}");
            }

            items.Add(new OrientationSnapshotItem(
                file.FullName,
                file.Length,
                file.LastWriteTimeUtc,
                GetApplyOrientation(item) ?? 0));
        }

        var snapshot = new OrientationSnapshot(reviewPath, DateTimeOffset.UtcNow, items);
        var path = ResolveArtifact(artifactRoot, "rotate", "orientation-snapshot.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(snapshot, JsonOptions),
            Encoding.UTF8,
            cancellationToken);
        return snapshot;
    }

    public async Task<OrientationApplyResult> ApplyAsync(
        string reviewPath,
        OrientationReviewReport report,
        OrientationSnapshot snapshot,
        IReadOnlyCollection<OrientationDecision> decisions,
        string artifactRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(snapshot.ReviewPath, reviewPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Apply refused; the confirmed snapshot belongs to another review report.");
        }

        ValidateSnapshot(snapshot);
        var decisionPath = ResolveArtifact(artifactRoot, "rotate", "orientation-decisions.json");
        var verificationPath = ResolveArtifact(artifactRoot, "rotate", "orientation-verification.json");
        var undoPath = ResolveArtifact(artifactRoot, "rotate", "orientation-undo.jsonl");
        var backupDirectory = ResolveArtifact(artifactRoot, "rotate", "backups");
        Directory.CreateDirectory(Path.GetDirectoryName(decisionPath)!);
        Directory.CreateDirectory(backupDirectory);
        await File.WriteAllTextAsync(
            decisionPath,
            JsonSerializer.Serialize(decisions, JsonOptions),
            Encoding.UTF8,
            cancellationToken);

        await RunPowerShellAsync(
            [
                "-File", FindScript(),
                "-ReviewPath", reviewPath,
                "-DecisionPath", decisionPath,
                "-OutputPath", reviewPath,
                "-UndoManifestPath", undoPath,
                "-VerificationReportPath", verificationPath,
                "-BackupDirectory", backupDirectory,
                "-Apply"
            ],
            cancellationToken);

        var verification = await File.ReadAllTextAsync(verificationPath, cancellationToken);
        using var document = JsonDocument.Parse(verification);
        var passed = document.RootElement.TryGetProperty("passed", out var passedElement)
            && passedElement.GetBoolean();
        var appliedCount = decisions.Count(decision =>
            string.Equals(decision.Action, "approve", StringComparison.OrdinalIgnoreCase));
        if (!passed)
        {
            throw new IOException("Orientation apply completed with a failed verification report.");
        }

        return new OrientationApplyResult(
            reviewPath, decisionPath, verificationPath, undoPath, appliedCount, passed);
    }

    public async Task<IReadOnlyList<OrientationUndoEntry>> ReadUndoEntriesAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        var entries = new List<OrientationUndoEntry>();
        await foreach (var line in File.ReadLinesAsync(manifestPath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<ManifestEntry>(line, JsonOptions);
            if (entry is not null)
            {
                entries.Add(new OrientationUndoEntry(
                    entry.Path,
                    entry.BackupPath,
                    entry.OrientationBefore,
                    entry.BeforeSha256,
                    entry.AfterSha256,
                    ParseOffset(entry.BeforeCreationTimeUtc),
                    ParseOffset(entry.BeforeLastWriteTimeUtc),
                    entry.VerificationPassed));
            }
        }

        return entries;
    }

    public async Task<IReadOnlyList<OrientationUndoEntry>> ReadActiveUndoEntriesAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        var entries = await ReadUndoEntriesAsync(manifestPath, cancellationToken);
        return entries
            .GroupBy(entry => Path.GetFullPath(entry.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Where(IsStillApplied)
            .ToArray();
    }

    public async Task UndoAsync(
        string manifestPath,
        IReadOnlyCollection<string> selectedPaths,
        string artifactRoot,
        CancellationToken cancellationToken = default)
    {
        var selected = selectedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedEntries = (await ReadActiveUndoEntriesAsync(manifestPath, cancellationToken))
            .Where(entry => selected.Contains(Path.GetFullPath(entry.Path)))
            .ToArray();
        if (selectedEntries.Length == 0)
        {
            throw new InvalidOperationException("Select at least one rotated file to undo.");
        }

        var temporaryManifest = ResolveArtifact(
            artifactRoot, "rotate", $"orientation-undo-selection-{Guid.NewGuid():N}.jsonl");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temporaryManifest)!);
            var lines = selectedEntries.Select(FormatUndoManifestLine);
            await File.WriteAllLinesAsync(temporaryManifest, lines, Utf8NoBom, cancellationToken);
            await RunPowerShellAsync(
                [
                    "-File", FindScript(),
                    "-Undo",
                    "-UndoManifestPath", temporaryManifest
                ],
                cancellationToken);
        }
        finally
        {
            if (File.Exists(temporaryManifest))
            {
                File.Delete(temporaryManifest);
            }
        }

        foreach (var entry in selectedEntries)
        {
            var restored = ReadContentEvidence(entry.Path);
            if (!string.Equals(restored.Sha256, entry.BeforeSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Undo verification failed; original bytes were not restored: {entry.Path}");
            }
        }
    }

    public static int? GetApplyOrientation(OrientationReviewItem item)
    {
        if (item.ProposedOrientation is >= 2 and <= 8)
        {
            return item.ProposedOrientation;
        }

        if (string.Equals(item.Status, "Proposed", StringComparison.OrdinalIgnoreCase)
            && item.Orientation is >= 2 and <= 8)
        {
            return item.Orientation;
        }

        return null;
    }

    public static bool IsApplyCandidate(OrientationReviewItem item) =>
        GetApplyOrientation(item) is not null
        && string.Equals(item.Status, "Proposed", StringComparison.OrdinalIgnoreCase);

    public string GetUndoManifestPath(string artifactRoot) =>
        ResolveArtifact(artifactRoot, "rotate", "orientation-undo.jsonl");

    internal static FileContentEvidence ReadContentEvidence(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new IOException($"File is missing: {path}");
        }

        using var stream = info.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new FileContentEvidence(info.Length, hash);
    }

    private void ValidateSnapshot(OrientationSnapshot snapshot)
    {
        foreach (var item in snapshot.Items)
        {
            var file = new FileInfo(item.Path);
            if (!file.Exists || file.Length != item.Size
                || !NearlyEqual(file.LastWriteTimeUtc, item.LastWriteTimeUtc.UtcDateTime))
            {
                throw new IOException($"Apply refused; snapshot changed: {item.Path}");
            }
        }
    }

    private string ResolveArtifact(string artifactRoot, params string[] parts)
    {
        var relative = Path.Combine(parts);
        return _pathPolicy.ResolveArtifactPath(artifactRoot, relative);
    }

    private string FindScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var current = directory; current is not null; current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "tools", "czkawka", "repair-orientation.ps1");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var workingDirectoryCandidate = Path.Combine(
            Directory.GetCurrentDirectory(), "tools", "czkawka", "repair-orientation.ps1");
        return File.Exists(workingDirectoryCandidate)
            ? workingDirectoryCandidate
            : throw new FileNotFoundException("The orientation repair script could not be located.");
    }

    private static async Task<OrientationReviewReport> ReadReportAsync(
        string reportPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(reportPath);
        return await JsonSerializer.DeserializeAsync<OrientationReviewReport>(
            stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The orientation review report was empty.");
    }

    private static async Task RunPowerShellAsync(
        IReadOnlyList<string> scriptArguments,
        CancellationToken cancellationToken)
    {
        Exception? lastStartException = null;
        foreach (var executable in new[] { "pwsh", "powershell" })
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            foreach (var argument in scriptArguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException($"Unable to start {executable}.");
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                lastStartException = exception;
                continue;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            _ = await outputTask;
            if (process.ExitCode is not 0 and not 11)
            {
                throw new InvalidOperationException(
                    $"Orientation repair script failed with exit code {process.ExitCode}: {error.Trim()}");
            }

            return;
        }

        throw new InvalidOperationException(
            "Neither pwsh nor Windows PowerShell could be started.", lastStartException);
    }

    internal static string FormatUndoManifestLine(OrientationUndoEntry entry) =>
        JsonSerializer.Serialize(
            new
            {
                path = entry.Path,
                backupPath = entry.BackupPath,
                orientationBefore = entry.OrientationBefore,
                beforeSha256 = entry.BeforeSha256,
                afterSha256 = entry.AfterSha256,
                beforeCreationTimeUtc = entry.BeforeCreationTimeUtc.ToString("o"),
                beforeLastWriteTimeUtc = entry.BeforeLastWriteTimeUtc.ToString("o"),
                verificationPassed = entry.VerificationPassed
            },
            JsonlOptions);

    private static bool IsStillApplied(OrientationUndoEntry entry)
    {
        try
        {
            if (!File.Exists(entry.Path) || !File.Exists(entry.BackupPath))
            {
                return false;
            }

            return string.Equals(
                ReadContentEvidence(entry.Path).Sha256,
                entry.AfterSha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static bool NearlyEqual(DateTime actual, DateTime expected) =>
        Math.Abs((actual.ToUniversalTime() - expected.ToUniversalTime()).TotalSeconds) <= 1;

    internal readonly record struct FileContentEvidence(long Size, string Sha256);

    private sealed class ManifestEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;
        [JsonPropertyName("backupPath")]
        public string BackupPath { get; init; } = string.Empty;
        [JsonPropertyName("orientationBefore")]
        public int OrientationBefore { get; init; }
        [JsonPropertyName("beforeSha256")]
        public string BeforeSha256 { get; init; } = string.Empty;
        [JsonPropertyName("afterSha256")]
        public string AfterSha256 { get; init; } = string.Empty;
        [JsonPropertyName("beforeCreationTimeUtc")]
        public string BeforeCreationTimeUtc { get; init; } = string.Empty;
        [JsonPropertyName("beforeLastWriteTimeUtc")]
        public string BeforeLastWriteTimeUtc { get; init; } = string.Empty;
        [JsonPropertyName("verificationPassed")]
        public bool VerificationPassed { get; init; }
    }

    private static DateTimeOffset ParseOffset(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
