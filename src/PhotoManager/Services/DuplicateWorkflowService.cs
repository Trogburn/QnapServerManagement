using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoManager.Models;

namespace PhotoManager.Services;

public sealed class DuplicateWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly PowerShellScriptRunner _runner;
    private readonly AtomicArtifactStore _store;
    private readonly PathPolicy _paths;
    private readonly string _repositoryRoot;
    private readonly string _scriptsRoot;

    public DuplicateWorkflowService(
        PowerShellScriptRunner runner,
        AtomicArtifactStore store,
        PathPolicy paths,
        string? repositoryRoot = null)
    {
        _runner = runner;
        _store = store;
        _paths = paths;
        _repositoryRoot = repositoryRoot ?? FindRepositoryRoot();
        _scriptsRoot = Path.Combine(_repositoryRoot, "tools", "czkawka");
    }

    public string RepositoryRoot => _repositoryRoot;

    public async Task<AppConfig> ConfigureAsync(
        WorkflowStateMachine workflow,
        string scanRoot,
        string quarantineRoot,
        string artifactRoot,
        CancellationToken cancellationToken = default)
    {
        PathPolicy.ValidateScanRoot(scanRoot);
        var quarantine = Path.GetFullPath(quarantineRoot);
        if (PathPolicy.IsWithin(quarantine, scanRoot) || PathPolicy.IsWithin(scanRoot, quarantine))
        {
            throw new ArgumentException("Quarantine must be separate from the source tree.");
        }

        var config = new AppConfig
        {
            ScanRoot = scanRoot,
            QuarantineRoot = quarantine,
            ArtifactRoot = artifactRoot,
            RepositoryRoot = _repositoryRoot
        };
        var session = workflow.Session;
        if (session.State == WorkflowState.Idle || session.State == WorkflowState.Completed || session.State == WorkflowState.Failed)
        {
            if (session.State != WorkflowState.Idle)
            {
                workflow.Reset();
            }
            workflow.TransitionTo(WorkflowState.Configured, "Source and quarantine configuration accepted.");
        }

        await _store.WriteAsync(
            artifactRoot,
            SessionName(workflow.Session) + "/config.json",
            "duplicate-session-config",
            config,
            cancellationToken);
        return config;
    }

    public async Task<DuplicateWorkflowArtifacts> PrepareAsync(
        WorkflowStateMachine workflow,
        AppConfig config,
        bool fresh,
        CancellationToken cancellationToken = default)
    {
        if (workflow.Session.State is not (WorkflowState.Configured
            or WorkflowState.DateReviewReady
            or WorkflowState.RotateReviewReady))
        {
            throw new InvalidOperationException(
                $"Workflow must be {WorkflowState.Configured}, {WorkflowState.DateReviewReady}, or {WorkflowState.RotateReviewReady} to start a duplicate scan.");
        }

        workflow.TransitionTo(WorkflowState.Scanning, "Starting read-only Czkawka scan.");
        var started = DateTime.UtcNow;
        await RunScriptAsync("scan.ps1", Args(
            ("-ScanRoot", config.ScanRoot),
            ("-ConfigPath", ConfigPath()),
            fresh ? ("-Fresh", null) : null,
            AllowLocalRootArg(config.ScanRoot)), cancellationToken);
        var scanDirectory = FindNewestScanDirectory(started);

        var normalized = Path.Combine(scanDirectory, "normalized", "combined.normalized.json");
        await RunScriptAsync("parse-results.ps1", Args(("-ScanReportDir", scanDirectory), ("-OutputPath", normalized)), cancellationToken);
        var classified = Path.Combine(scanDirectory, "classified.json");
        await RunScriptAsync("classify-results.ps1", Args(("-InputPath", normalized), ("-OutputPath", classified), ("-ConfigPath", ConfigPath())), cancellationToken);

        var reviewDirectory = Path.Combine(scanDirectory, "review");
        var decisions = Path.Combine(reviewDirectory, "decisions.json");
        var reviewJson = Path.Combine(reviewDirectory, "review.json");
        var reviewHtml = Path.Combine(reviewDirectory, "review.html");
        Directory.CreateDirectory(reviewDirectory);
        await RunScriptAsync("review.ps1", Args(
            ("-InputPath", classified),
            ("-DecisionPath", decisions),
            ("-HtmlReportPath", reviewHtml),
            ("-JsonReportPath", reviewJson),
            ("-ExportOnly", null)), cancellationToken);
        if (!File.Exists(decisions))
        {
            await File.WriteAllTextAsync(decisions, "[]", cancellationToken);
        }

        var artifacts = new DuplicateWorkflowArtifacts(
            scanDirectory, normalized, classified, decisions, reviewJson, reviewHtml,
            Path.Combine(scanDirectory, "remediation-dry-run.json"),
            null, null);
        workflow.TransitionTo(WorkflowState.ScanReady, "Scan and artifacts created.", scanDirectory);
        workflow.TransitionTo(WorkflowState.Reviewing, "Review archive exported.", scanDirectory, reviewJson);
        workflow.SetDuplicateArtifacts(artifacts);
        return artifacts;
    }

    public async Task<DuplicateWorkflowArtifacts> RunDryRunAsync(
        WorkflowStateMachine workflow,
        AppConfig config,
        DuplicateWorkflowArtifacts artifacts,
        CancellationToken cancellationToken = default)
    {
        RequireState(workflow, WorkflowState.Reviewing);
        EnsureArtifacts(artifacts);
        var validation = await ValidateReviewAsync(artifacts, cancellationToken);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Duplicate review is incomplete: {validation.ResolvedGroupCount} of {validation.TotalGroupCount} groups resolved. " +
                $"Unresolved groups: {string.Join(", ", validation.UnresolvedGroupIds)}.");
        }
        var freeze = await CreateFreezeAsync(workflow, config, artifacts, cancellationToken);
        var output = await RunScriptAsync("remediate.ps1", Args(
            ("-InputPath", artifacts.ClassifiedPath),
            ("-DecisionPath", artifacts.DecisionPath),
            ("-QuarantineRoot", config.QuarantineRoot),
            ("-ScanRoot", config.ScanRoot),
            ("-ConfigPath", ConfigPath()),
            ("-TransactionManifestPath", TransactionPath(config)),
            null), cancellationToken);
        await File.WriteAllTextAsync(artifacts.DryRunPath, output, cancellationToken);
        var updated = artifacts with { FreezePath = freeze };
        workflow.TransitionTo(WorkflowState.RemediationReady, "Dry-run completed; snapshot is frozen.", artifacts.ScanDirectory, artifacts.ReviewJsonPath);
        workflow.SetDuplicateArtifacts(updated);
        return updated;
    }

    public async Task<DuplicateReviewValidationResult> ValidateReviewAsync(
        DuplicateWorkflowArtifacts artifacts,
        CancellationToken cancellationToken = default)
    {
        EnsureArtifacts(artifacts);

        using var classifiedDocument = await ReadJsonAsync(artifacts.ClassifiedPath, cancellationToken);
        if (classifiedDocument.RootElement.ValueKind != JsonValueKind.Object ||
            classifiedDocument.RootElement.GetProperty("schemaVersion").GetInt32() != 1 ||
            !string.Equals(
                classifiedDocument.RootElement.GetProperty("source").GetString(),
                "classifier",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Classified duplicate artifact is not a schema version 1 classifier document.");
        }

        using var decisionsDocument = await ReadJsonAsync(artifacts.DecisionPath, cancellationToken);
        JsonElement[] decisions;
        if (decisionsDocument.RootElement.ValueKind == JsonValueKind.Array)
        {
            decisions = decisionsDocument.RootElement.EnumerateArray()
                .Where(decision => decision.ValueKind == JsonValueKind.Object)
                .ToArray();
        }
        else if (decisionsDocument.RootElement.ValueKind == JsonValueKind.Object)
        {
            decisions = [decisionsDocument.RootElement];
        }
        else
        {
            throw new InvalidDataException("Duplicate decisions artifact must contain a JSON object or array.");
        }
        var unresolved = new List<string>();
        var total = 0;

        foreach (var group in GetRequiredArray(classifiedDocument.RootElement, "groups"))
        {
            total++;
            var groupId = GetString(group, "groupId");
            if (string.IsNullOrWhiteSpace(groupId))
            {
                unresolved.Add($"group-{total}");
                continue;
            }

            var itemPaths = GetRequiredArray(group, "items")
                .Select(item => GetString(item, "path"))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var suggestedKeepPath = GetString(group, "suggestedKeepPath");
            var nonKeeperPaths = itemPaths
                .Where(path => !string.Equals(path, suggestedKeepPath, StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var groupDecisions = decisions
                .Where(decision => string.Equals(GetString(decision, "groupId"), groupId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var resolvedByGroupAction = groupDecisions.Any(decision =>
            {
                var action = GetString(decision, "action");
                if (string.Equals(action, "defer", StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrWhiteSpace(GetString(decision, "path"));
                }

                if (!string.Equals(action, "keep", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrWhiteSpace(GetString(decision, "path")))
                {
                    return false;
                }

                var keepPath = GetString(decision, "keepPath");
                return !string.IsNullOrWhiteSpace(keepPath) &&
                       itemPaths.Contains(keepPath);
            });
            var resolvedByItemActions = nonKeeperPaths.Count > 0 &&
                nonKeeperPaths.All(path => groupDecisions.Any(decision =>
                    string.Equals(GetString(decision, "action"), "quarantine-requested", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(GetString(decision, "path"), path, StringComparison.OrdinalIgnoreCase) &&
                    IsSha256(GetString(decision, "sha256"))));

            if (!resolvedByGroupAction && !resolvedByItemActions)
            {
                unresolved.Add(groupId);
            }
        }

        return new DuplicateReviewValidationResult(total - unresolved.Count, total, unresolved);
    }

    public async Task<string> ApplyAsync(
        WorkflowStateMachine workflow,
        AppConfig config,
        DuplicateWorkflowArtifacts artifacts,
        bool snapshotConfirmed,
        CancellationToken cancellationToken = default)
    {
        RequireState(workflow, WorkflowState.RemediationReady);
        if (!snapshotConfirmed || !workflow.Session.SnapshotConfirmed)
        {
            throw new InvalidOperationException("Apply requires explicit confirmation of the frozen dry-run snapshot.");
        }
        EnsureArtifacts(artifacts);
        if (artifacts.FreezePath is null || !File.Exists(artifacts.FreezePath))
        {
            throw new InvalidOperationException("Apply is blocked because no artifact freeze exists.");
        }
        await VerifyFreezeAsync(artifacts.FreezePath, config, artifacts, cancellationToken);
        var output = await RunScriptAsync("remediate.ps1", Args(
            ("-InputPath", artifacts.ClassifiedPath),
            ("-DecisionPath", artifacts.DecisionPath),
            ("-QuarantineRoot", config.QuarantineRoot),
            ("-ScanRoot", config.ScanRoot),
            ("-ConfigPath", ConfigPath()),
            ("-TransactionManifestPath", TransactionPath(config)),
            ("-Apply", null)), cancellationToken);
        var applyLog = Path.Combine(artifacts.ScanDirectory, "apply-result.txt");
        await File.WriteAllTextAsync(applyLog, output, cancellationToken);
        workflow.SetDuplicateArtifacts(artifacts with { ApplyLogPath = applyLog });
        workflow.TransitionTo(
            WorkflowState.RemediationApplied,
            "Duplicate files moved to quarantine; verification is required.",
            artifacts.ScanDirectory,
            artifacts.ReviewJsonPath);
        return applyLog;
    }

    public async Task<string> VerifyAsync(
        WorkflowStateMachine workflow,
        AppConfig config,
        DuplicateWorkflowArtifacts artifacts,
        CancellationToken cancellationToken = default)
    {
        RequireState(workflow, WorkflowState.RemediationApplied);
        var outputDirectory = Path.Combine(artifacts.ScanDirectory, "verification");
        var reportPath = Path.Combine(outputDirectory, "verification.json");
        var result = await _runner.RunAsync(
            Path.Combine(_scriptsRoot, "verify-remediation.ps1"),
            Args(
                ("-InputPath", artifacts.ClassifiedPath),
                ("-DecisionPath", artifacts.DecisionPath),
                ("-TransactionManifestPath", TransactionPath(config)),
                ("-ScanRoot", config.ScanRoot),
                ("-QuarantineRoot", config.QuarantineRoot),
                ("-ConfigPath", ConfigPath()),
                ("-OutputDirectory", outputDirectory),
                AllowLocalRootArg(config.ScanRoot),
                null).Where(argument => argument is not null).Select(argument => argument!),
            _repositoryRoot,
            cancellationToken);
        if (!File.Exists(reportPath))
        {
            var detail = string.Join(
                Environment.NewLine,
                new[] { result.StandardError, result.StandardOutput }.Where(text => !string.IsNullOrWhiteSpace(text)));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? "Verify quarantine did not write a report."
                    : $"Verify quarantine did not write a report. {detail.Trim()}");
        }

        using var report = await ReadJsonAsync(reportPath, cancellationToken);
        if (!report.RootElement.TryGetProperty("passed", out var passed) || !passed.GetBoolean())
        {
            var failures = report.RootElement.TryGetProperty("failures", out var list)
                ? list.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToArray()
                : [];
            throw new InvalidOperationException(
                failures.Length == 0
                    ? "Quarantine verification failed."
                    : "Quarantine verification found " +
                      $"{failures.Length} issue(s): {string.Join("; ", failures)}");
        }

        workflow.TransitionTo(WorkflowState.Completed, "Remediation verification completed.");
        return reportPath;
    }

    public async Task<string> ReadScanIdAsync(
        string classifiedPath,
        CancellationToken cancellationToken = default)
    {
        using var document = await ReadJsonAsync(classifiedPath, cancellationToken);
        var stated = document.RootElement.TryGetProperty("scanId", out var scanId) &&
                     scanId.ValueKind == JsonValueKind.String
            ? scanId.GetString()
            : null;
        return ResolveScanId(classifiedPath, stated);
    }

    public async Task<IReadOnlyList<DuplicateTransactionEntry>> ReadActiveTransactionsAsync(
        AppConfig config,
        string scanId,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = TransactionPath(config);
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        var expectedScanId = string.IsNullOrWhiteSpace(scanId) ? string.Empty : scanId.Trim();
        var active = new List<DuplicateTransactionEntry>();
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(manifestPath, cancellationToken))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            DuplicateTransactionEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<DuplicateTransactionEntry>(line, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Duplicate transaction manifest contains invalid JSON on line {lineNumber}.", exception);
            }

            if (entry is null || string.IsNullOrWhiteSpace(entry.Source) ||
                string.IsNullOrWhiteSpace(entry.Destination))
            {
                continue;
            }

            var entryScanId = string.IsNullOrWhiteSpace(entry.ScanId) ? string.Empty : entry.ScanId.Trim();
            if (!string.Equals(entryScanId, expectedScanId, StringComparison.Ordinal))
            {
                continue;
            }

            var key = TransactionKey(entry.Source, entry.Destination);
            if (string.Equals(entry.Status, "moved", StringComparison.OrdinalIgnoreCase))
            {
                active.Add(entry);
            }
            else if (string.Equals(entry.Status, "undone", StringComparison.OrdinalIgnoreCase))
            {
                var match = active.FindLastIndex(item =>
                    string.Equals(TransactionKey(item.Source, item.Destination), key, StringComparison.OrdinalIgnoreCase));
                if (match >= 0)
                {
                    active.RemoveAt(match);
                }
            }
        }

        return active
            .OrderByDescending(entry => entry.TransactionUtc)
            .ToArray();
    }

    public async Task<string> UndoSelectedAsync(
        AppConfig config,
        DuplicateWorkflowArtifacts artifacts,
        IReadOnlyCollection<string> selectedSourcePaths,
        CancellationToken cancellationToken = default)
    {
        if (selectedSourcePaths.Count == 0)
        {
            throw new InvalidOperationException("Select at least one duplicate transaction to undo.");
        }

        EnsureArtifacts(artifacts);
        var scanId = await ReadScanIdAsync(artifacts.ClassifiedPath, cancellationToken);
        var active = await ReadActiveTransactionsAsync(config, scanId, cancellationToken);
        var selected = selectedSourcePaths
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedEntries = active
            .Where(entry => selected.Contains(Path.GetFullPath(entry.Source)))
            .ToArray();
        if (selectedEntries.Length != selected.Count)
        {
            throw new InvalidOperationException(
                "The selected duplicate transactions are no longer active. Refresh the undo list and try again.");
        }

        var listPath = Path.Combine(Path.GetTempPath(), $"photo-duplicate-undo-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(
                listPath,
                selectedEntries.Select(entry => entry.Source),
                cancellationToken);
            var undoArguments = Args(
                ("-InputPath", artifacts.ClassifiedPath),
                ("-DecisionPath", artifacts.DecisionPath),
                ("-QuarantineRoot", config.QuarantineRoot),
                ("-ScanRoot", config.ScanRoot),
                ("-ConfigPath", ConfigPath()),
                ("-TransactionManifestPath", TransactionPath(config)),
                ("-ScanId", scanId),
                ("-Undo", null),
                ("-UndoSourcePathFile", listPath),
                null);
            var output = await RunScriptAsync("remediate.ps1", undoArguments, cancellationToken);

            var remaining = await ReadActiveTransactionsAsync(config, scanId, cancellationToken);
            var remainingKeys = remaining
                .Select(entry => TransactionKey(entry.Source, entry.Destination))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedEntries.Any(entry => remainingKeys.Contains(TransactionKey(entry.Source, entry.Destination))))
            {
                throw new InvalidOperationException("Selective duplicate undo completed without clearing every selected transaction.");
            }
            foreach (var entry in selectedEntries)
            {
                if (!File.Exists(entry.Source) || File.Exists(entry.Destination))
                {
                    throw new InvalidOperationException(
                        "Selective duplicate undo did not restore every selected source and clear its quarantine path.");
                }

                if (entry.PreMove is null || string.IsNullOrWhiteSpace(entry.PreMove.Sha256))
                {
                    throw new InvalidOperationException(
                        $"Undo verification failed; pre-move evidence is missing for {entry.Source}.");
                }

                await using var stream = File.OpenRead(entry.Source);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                    .ToLowerInvariant();
                var info = new FileInfo(entry.Source);
                if (info.Length != entry.PreMove.Size ||
                    !hash.Equals(entry.PreMove.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Undo verification failed; restored file does not match pre-move evidence: {entry.Source}");
                }
            }

            return output;
        }
        finally
        {
            if (File.Exists(listPath))
            {
                File.Delete(listPath);
            }
        }
    }

    public void ConfirmSnapshot(WorkflowStateMachine workflow, bool confirmed)
    {
        if (workflow.Session.State != WorkflowState.RemediationReady)
        {
            throw new InvalidOperationException("A snapshot can only be confirmed after a dry-run.");
        }
        workflow.SetSnapshotConfirmed(confirmed);
    }

    public void OpenReviewer(DuplicateWorkflowArtifacts artifacts)
    {
        EnsureArtifacts(artifacts);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = _repositoryRoot,
            UseShellExecute = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(_scriptsRoot, "review.ps1"));
        startInfo.ArgumentList.Add("-InputPath");
        startInfo.ArgumentList.Add(artifacts.ClassifiedPath);
        startInfo.ArgumentList.Add("-DecisionPath");
        startInfo.ArgumentList.Add(artifacts.DecisionPath);
        startInfo.ArgumentList.Add("-HtmlReportPath");
        startInfo.ArgumentList.Add(artifacts.ReviewHtmlPath);
        startInfo.ArgumentList.Add("-JsonReportPath");
        startInfo.ArgumentList.Add(artifacts.ReviewJsonPath);

        if (System.Diagnostics.Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("Unable to start the duplicate reviewer.");
        }
    }

    private async Task<string> CreateFreezeAsync(
        WorkflowStateMachine workflow,
        AppConfig config,
        DuplicateWorkflowArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        var paths = new[] { artifacts.NormalizedPath, artifacts.ClassifiedPath, artifacts.DecisionPath, artifacts.ReviewJsonPath };
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths.Where(File.Exists))
        {
            await using var stream = File.OpenRead(path);
            hashes[path] = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        if (!hashes.ContainsKey(artifacts.ClassifiedPath))
        {
            throw new InvalidOperationException("Cannot freeze missing classified artifacts.");
        }
        var freeze = new ArtifactFreeze(workflow.Session.Id.ToString("N"), DateTimeOffset.UtcNow,
            config.ScanRoot, config.QuarantineRoot, hashes);
        var freezePath = Path.Combine(artifacts.ScanDirectory, "artifact-freeze.json");
        await File.WriteAllTextAsync(freezePath, JsonSerializer.Serialize(freeze, JsonOptions), cancellationToken);
        return freezePath;
    }

    private static async Task VerifyFreezeAsync(string freezePath, AppConfig config, DuplicateWorkflowArtifacts artifacts, CancellationToken cancellationToken)
    {
        var freeze = JsonSerializer.Deserialize<ArtifactFreeze>(await File.ReadAllTextAsync(freezePath, cancellationToken), JsonOptions)
            ?? throw new InvalidOperationException("Artifact freeze is invalid.");
        if (!string.Equals(freeze.ScanRoot, config.ScanRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(freeze.QuarantineRoot, config.QuarantineRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Configuration changed after the snapshot was frozen.");
        }
        foreach (var pair in freeze.Sha256ByPath)
        {
            if (!File.Exists(pair.Key)) throw new InvalidOperationException($"Frozen artifact is missing: {pair.Key}");
            await using var stream = File.OpenRead(pair.Key);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            if (!hash.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Frozen artifact changed: {pair.Key}");
        }
    }

    private async Task<string> RunScriptAsync(string script, IEnumerable<string?> arguments, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(Path.Combine(_scriptsRoot, script), arguments.Where(x => x is not null)!.Select(x => x!), _repositoryRoot, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{script} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
        return result.StandardOutput + Environment.NewLine + result.StandardError;
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Duplicate artifact contains invalid JSON: {path}", exception);
        }
    }

    private static IEnumerable<JsonElement> GetRequiredArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Duplicate artifact is missing array property '{propertyName}'.");
        }
        return property.EnumerateArray();
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool IsSha256(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == 64 &&
        value.All(character => Uri.IsHexDigit(character));

    internal static string ResolveScanId(string classifiedPath, string? classifiedScanId)
    {
        if (!string.IsNullOrWhiteSpace(classifiedScanId))
        {
            return classifiedScanId.Trim();
        }

        var fullPath = Path.GetFullPath(classifiedPath);
        var parent = Path.GetFileName(Path.GetDirectoryName(fullPath));
        if (!string.IsNullOrWhiteSpace(parent) &&
            parent.StartsWith("scan-", StringComparison.OrdinalIgnoreCase))
        {
            return parent;
        }

        var normalizedParent = Path.GetFileName(Path.GetDirectoryName(fullPath));
        var scanFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(fullPath)));
        if (string.Equals(normalizedParent, "normalized", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(scanFolder) &&
            scanFolder.StartsWith("scan-", StringComparison.OrdinalIgnoreCase))
        {
            return scanFolder;
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath.ToLowerInvariant())))
            .ToLowerInvariant();
        return "classified-" + fingerprint[..12];
    }

    private static string TransactionKey(string source, string destination) =>
        $"{Path.GetFullPath(source)}\n{Path.GetFullPath(destination)}";

    private string FindNewestScanDirectory(DateTime started)
    {
        var reportRoot = ReadReportRoot();
        var directory = new DirectoryInfo(reportRoot).GetDirectories("scan-*")
            .Where(x => x.LastWriteTimeUtc >= started.AddSeconds(-5))
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .FirstOrDefault();
        return directory?.FullName ?? throw new InvalidOperationException("Scan completed but no scan artifact directory was found.");
    }

    private string ReadReportRoot()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath()));
        var configured = document.RootElement.GetProperty("scan").GetProperty("localReportRoot").GetString()
            ?? throw new InvalidOperationException("Czkawka report root is not configured.");
        return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(_repositoryRoot, configured));
    }

    private string ConfigPath() => Path.Combine(_scriptsRoot, "config.json");
    private static string TransactionPath(AppConfig config) => Path.Combine(config.QuarantineRoot, "transactions.jsonl");
    private static string SessionName(WorkflowSession session) => Path.Combine("sessions", session.Id.ToString("N"));
    private static void RequireState(WorkflowStateMachine workflow, WorkflowState expected)
    {
        if (workflow.Session.State != expected) throw new InvalidOperationException($"Workflow must be {expected}; it is {workflow.Session.State}.");
    }
    private static void EnsureArtifacts(DuplicateWorkflowArtifacts artifacts)
    {
        foreach (var path in new[] { artifacts.NormalizedPath, artifacts.ClassifiedPath, artifacts.DecisionPath, artifacts.ReviewJsonPath })
            if (!File.Exists(path)) throw new InvalidOperationException($"Required workflow artifact is missing: {path}");
    }
    private static (string? Name, string? Value)? AllowLocalRootArg(string scanRoot) =>
        PathPolicy.RequiresAllowLocalRoot(scanRoot) ? ("-AllowLocalRoot", null) : null;

    private static IEnumerable<string?> Args(params (string? Name, string? Value)?[] values)
    {
        foreach (var item in values)
        {
            if (item is null) { continue; }
            var (name, value) = item.Value;
            if (name is not null) { yield return name; }
            if (value is not null) { yield return value; }
        }
    }
    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "tools", "czkawka", "scan.ps1"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository tools/czkawka.");
    }
}
