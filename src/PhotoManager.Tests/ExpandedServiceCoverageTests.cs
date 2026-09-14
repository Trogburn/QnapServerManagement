using System.Text.Json;
using PhotoManager.Models;
using PhotoManager.Services;
using Xunit;

namespace PhotoManager.Tests;

public sealed class PathPolicyCoverageTests : TestBase
{
    [Fact]
    public void ConstructorRejectsNullAndNormalizesRoot()
    {
        Assert.Throws<ArgumentNullException>(() => new PathPolicy(null!));

        var root = NewTempDirectory();
        try
        {
            var policy = new PathPolicy(Path.Combine(root, "."));
            Assert.Equal(Path.GetFullPath(root), policy.ApplicationRoot);
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(@"C:\absolute.json")]
    public void ResolveArtifactPathRejectsInvalidNames(string name)
    {
        var root = NewTempDirectory();
        try
        {
            Assert.Throws<ArgumentException>(() =>
                new PathPolicy(root).ResolveArtifactPath("artifacts", name));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ResolveArtifactPathRejectsTraversalAndAllowsNestedRelativePath()
    {
        var root = NewTempDirectory();
        try
        {
            var policy = new PathPolicy(root);
            Assert.Throws<UnauthorizedAccessException>(() =>
                policy.ResolveArtifactPath("artifacts", @"..\outside.json"));

            var expected = Path.Combine(Path.GetFullPath(root), "artifacts", "nested", "item.json");
            Assert.Equal(expected, policy.ResolveArtifactPath("artifacts", @"nested\item.json"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void IsWithinHandlesEqualDescendantAndSiblingPaths()
    {
        var root = NewTempDirectory();
        try
        {
            Assert.True(PathPolicy.IsWithin(root, root));
            Assert.True(PathPolicy.IsWithin(Path.Combine(root, "child"), root));
            Assert.False(PathPolicy.IsWithin(root + "-sibling", root));
        }
        finally { Delete(root); }
    }
}

public sealed class AtomicArtifactStoreCoverageTests : TestBase
{
    [Fact]
    public async Task WriteReadCreatesDirectoriesAndPreservesPayload()
    {
        var root = NewTempDirectory();
        try
        {
            var store = new AtomicArtifactStore(new PathPolicy(root));
            var payload = new[] { "one", "two" };
            var path = await store.WriteAsync("artifacts", @"nested\payload.json", "test", payload);

            Assert.True(File.Exists(path));
            Assert.Equal(payload, await store.ReadAsync<string[]>("artifacts", @"nested\payload.json"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task WriteRejectsExistingImmutableArtifactAndLeavesOriginal()
    {
        var root = NewTempDirectory();
        try
        {
            var store = new AtomicArtifactStore(new PathPolicy(root));
            var path = await store.WriteAsync("artifacts", "immutable.json", "test", "first");
            await Assert.ThrowsAsync<IOException>(() =>
                store.WriteAsync("artifacts", "immutable.json", "test", "second"));
            Assert.Contains("first", await File.ReadAllTextAsync(path));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ReadRejectsInvalidEnvelopeAndInvalidDigest()
    {
        var root = NewTempDirectory();
        try
        {
            var store = new AtomicArtifactStore(new PathPolicy(root));
            var policy = new PathPolicy(root);
            var invalid = policy.ResolveArtifactPath("artifacts", "invalid.json");
            Directory.CreateDirectory(Path.GetDirectoryName(invalid)!);
            await File.WriteAllTextAsync(invalid, "{not-json");
            await Assert.ThrowsAnyAsync<Exception>(() => store.ReadAsync<string>("artifacts", "invalid.json"));

            var digestPath = policy.ResolveArtifactPath("artifacts", "digest.json");
            await File.WriteAllTextAsync(digestPath,
                """{"schemaVersion":1,"artifactType":"test","createdUtc":"2026-01-01T00:00:00Z","payload":"x","payloadSha256":"not-hex"}""");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReadAsync<string>("artifacts", "digest.json"));
        }
        finally { Delete(root); }
    }
}

public sealed class WorkflowStateMachineCoverageTests : TestBase
{
    [Fact]
    public void ValidTransitionBranchesRecordHistoryAndCarryArtifacts()
    {
        var workflow = new WorkflowStateMachine();
        workflow.TransitionTo(WorkflowState.Configured, "configured");
        workflow.TransitionTo(WorkflowState.Scanning, "scan");
        workflow.TransitionTo(WorkflowState.ScanReady, "ready", "scan-dir");
        workflow.TransitionTo(WorkflowState.Reviewing, "review", reviewArtifactPath: "review.json");
        workflow.TransitionTo(WorkflowState.DateReviewReady);
        workflow.TransitionTo(WorkflowState.Reviewing);
        workflow.TransitionTo(WorkflowState.RemediationReady);
        workflow.SetSnapshotConfirmed(true);
        workflow.TransitionTo(WorkflowState.RemediationApplied);
        workflow.TransitionTo(WorkflowState.Completed);
        workflow.TransitionTo(WorkflowState.RemediationApplied, "undo after verify");
        workflow.TransitionTo(WorkflowState.Completed);
        workflow.TransitionTo(WorkflowState.Configured);

        Assert.Equal(WorkflowState.Configured, workflow.Session.State);
        Assert.Equal(12, workflow.History.Count);
        Assert.Equal("scan-dir", workflow.Session.ScanArtifactPath);
        Assert.Equal("review.json", workflow.Session.ReviewArtifactPath);
        Assert.False(workflow.Session.SnapshotConfirmed);
        Assert.Equal("configured", workflow.History[0].Reason);
    }

    [Fact]
    public void DateReviewReadyCanStartADuplicateScan()
    {
        var workflow = new WorkflowStateMachine();
        workflow.TransitionTo(WorkflowState.Configured);
        workflow.TransitionTo(WorkflowState.Scanning);
        workflow.TransitionTo(WorkflowState.ScanReady);
        workflow.TransitionTo(WorkflowState.DateReviewReady);

        workflow.TransitionTo(WorkflowState.Scanning, "sibling duplicate scan");

        Assert.Equal(WorkflowState.Scanning, workflow.Session.State);
        Assert.Equal("sibling duplicate scan", workflow.History[^1].Reason);
    }

    [Fact]
    public void RotateReviewReadyCanStartADuplicateScan()
    {
        var workflow = new WorkflowStateMachine();
        workflow.TransitionTo(WorkflowState.Configured);
        workflow.TransitionTo(WorkflowState.Scanning);
        workflow.TransitionTo(WorkflowState.ScanReady);
        workflow.TransitionTo(WorkflowState.RotateReviewReady);

        workflow.TransitionTo(WorkflowState.Scanning, "sibling duplicate scan");

        Assert.Equal(WorkflowState.Scanning, workflow.Session.State);
    }

    [Fact]
    public void FailedBranchSetsErrorAndCanRecover()
    {
        var workflow = new WorkflowStateMachine();
        workflow.TransitionTo(WorkflowState.Failed, "boom");
        Assert.Equal("boom", workflow.Session.Error);
        Assert.Equal(WorkflowState.Failed, workflow.History.Single().To);

        workflow.TransitionTo(WorkflowState.Configured);
        Assert.Null(workflow.Session.Error);
    }

    [Fact]
    public void InvalidTransitionsAndSnapshotConfirmationAreRejected()
    {
        var workflow = new WorkflowStateMachine();
        Assert.Throws<InvalidOperationException>(() => workflow.TransitionTo(WorkflowState.Completed));
        Assert.Throws<InvalidOperationException>(() => workflow.SetSnapshotConfirmed(true));
        workflow.TransitionTo(WorkflowState.Configured);
        Assert.Throws<InvalidOperationException>(() => workflow.TransitionTo(WorkflowState.Idle));
    }

    [Fact]
    public void SettersUpdateSessionAndResetClearsLifecycle()
    {
        var workflow = new WorkflowStateMachine();
        var artifacts = new DuplicateWorkflowArtifacts("scan", "normalized", "classified", "decisions", "review", "html", "dry");
        var updated = workflow.SetDuplicateArtifacts(artifacts);
        Assert.Same(artifacts, updated.DuplicateArtifacts);
        Assert.True(updated.UpdatedUtc >= updated.CreatedUtc);

        workflow.TransitionTo(WorkflowState.Configured);
        Assert.NotEmpty(workflow.History);
        var oldId = workflow.Session.Id;
        workflow.Reset();
        Assert.Equal(WorkflowState.Idle, workflow.Session.State);
        Assert.NotEqual(oldId, workflow.Session.Id);
        Assert.Empty(workflow.History);
        Assert.Null(workflow.Session.DuplicateArtifacts);
    }
}

public sealed class DateRepairServiceCoverageTests : TestBase
{
    [Fact]
    public async Task CreateSnapshotIncludesOnlyEligibleItemsAndWritesSnapshot()
    {
        var root = NewTempDirectory();
        try
        {
            var file = Path.Combine(root, "photo.jpg");
            await File.WriteAllTextAsync(file, "photo");
            var info = new FileInfo(file);
            var report = new DateReviewReport
            {
                Items =
                [
                    new DateReviewItem
                    {
                        Path = file, Size = info.Length, Status = "Proposed",
                        ProposedCaptureTimeUtc = "2026-01-01T00:00:00Z",
                        CurrentLastWriteTimeUtc = info.LastWriteTimeUtc.ToString("O")
                    },
                    new DateReviewItem { Path = Path.Combine(root, "skipped.jpg"), Status = "Skipped" }
                ]
            };
            var service = new DateRepairService(new PathPolicy(root));
            var snapshot = await service.CreateSnapshotAsync("review.json", report, "artifacts");

            Assert.Single(snapshot.Items);
            Assert.Equal(Path.GetFullPath(file), snapshot.Items[0].Path);
            Assert.True(File.Exists(Path.Combine(root, "artifacts", "dates", "date-snapshot.json")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task CreateSnapshotRejectsMissingOrStaleEligibleFiles()
    {
        var root = NewTempDirectory();
        try
        {
            var service = new DateRepairService(new PathPolicy(root));
            var missing = new DateReviewReport
            {
                Items = [new DateReviewItem
                {
                    Path = Path.Combine(root, "missing.jpg"), Size = 1, Status = "Proposed",
                    ProposedCaptureTimeUtc = "2026-01-01T00:00:00Z",
                    CurrentLastWriteTimeUtc = DateTime.UtcNow.ToString("O")
                }]
            };
            await Assert.ThrowsAsync<IOException>(() =>
                service.CreateSnapshotAsync("review", missing, "artifacts"));

            var file = Path.Combine(root, "stale.jpg");
            await File.WriteAllTextAsync(file, "actual");
            var stale = new DateReviewReport { Items = [new DateReviewItem
            {
                Path = file, Size = 999, Status = "Proposed",
                ProposedCaptureTimeUtc = "2026-01-01T00:00:00Z",
                CurrentLastWriteTimeUtc = DateTime.UtcNow.ToString("O")
            }] };
            await Assert.ThrowsAsync<IOException>(() =>
                service.CreateSnapshotAsync("review", stale, "artifacts"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ReadUndoEntriesSkipsBlankLinesAndUndoRejectsNoSelection()
    {
        var root = NewTempDirectory();
        try
        {
            var manifest = Path.Combine(root, "undo.jsonl");
            await File.WriteAllTextAsync(manifest,
                Environment.NewLine + """{"path":"C:\\photo.jpg","beforeCreationTimeUtc":"2026-01-01T00:00:00Z","beforeLastWriteTimeUtc":"2026-01-01T00:00:00Z","afterCreationTimeUtc":"2026-01-02T00:00:00Z","afterLastWriteTimeUtc":"2026-01-02T00:00:00Z","verificationPassed":true}""");
            var service = new DateRepairService(new PathPolicy(root));
            var entries = await service.ReadUndoEntriesAsync(manifest);
            Assert.Single(entries);
            Assert.True(entries[0].VerificationPassed);
            Assert.Empty(await service.ReadActiveUndoEntriesAsync(manifest));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UndoAsync(manifest, [], "artifacts"));
            Assert.Empty(await service.ReadUndoEntriesAsync(Path.Combine(root, "missing.jsonl")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void UndoSelectionManifestIsSingleLineJson()
    {
        var entry = new DateUndoEntry(
            @"\\server\share\photo.jpg",
            DateTimeOffset.Parse("2026-09-13T06:57:59.3736944Z"),
            DateTimeOffset.Parse("2026-09-13T07:30:01.4553407Z"),
            DateTimeOffset.Parse("2026-01-01T06:00:00.0000000Z"),
            DateTimeOffset.Parse("2026-09-13T07:30:01.4553407Z"),
            true);

        var line = DateRepairService.FormatUndoManifestLine(entry);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        using var document = JsonDocument.Parse(line);
        Assert.Equal(entry.Path, document.RootElement.GetProperty("path").GetString());
        Assert.True(document.RootElement.GetProperty("verificationPassed").GetBoolean());
    }

    [Fact]
    public async Task SelectiveUndoRestoresOnlyCheckedFileFromJsonlManifest()
    {
        var root = NewTempDirectory();
        try
        {
            var photo = Path.Combine(root, "keep.jpg");
            await File.WriteAllBytesAsync(photo, [0xFF, 0xD8, 0xFF, 0xD9]);
            var beforeCreation = new DateTime(2026, 9, 13, 6, 57, 59, DateTimeKind.Utc);
            var afterCreation = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);
            var writeTime = new DateTime(2026, 9, 13, 7, 30, 1, DateTimeKind.Utc);
            File.SetCreationTimeUtc(photo, afterCreation);
            File.SetLastWriteTimeUtc(photo, writeTime);

            var manifest = Path.Combine(root, "undo.jsonl");
            var entry = new DateUndoEntry(
                photo,
                new DateTimeOffset(beforeCreation),
                new DateTimeOffset(writeTime),
                new DateTimeOffset(afterCreation),
                new DateTimeOffset(writeTime),
                true);
            await File.WriteAllTextAsync(manifest, DateRepairService.FormatUndoManifestLine(entry) + Environment.NewLine);

            var service = new DateRepairService(new PathPolicy(root));
            Assert.Single(await service.ReadActiveUndoEntriesAsync(manifest));
            await service.UndoAsync(manifest, [photo], Path.Combine(root, "artifacts"));

            Assert.Equal(beforeCreation, File.GetCreationTimeUtc(photo), TimeSpan.FromSeconds(1));
            Assert.Equal(writeTime, File.GetLastWriteTimeUtc(photo), TimeSpan.FromSeconds(1));
            Assert.Empty(await service.ReadActiveUndoEntriesAsync(manifest));
            Assert.Single(await service.ReadUndoEntriesAsync(manifest));
            var evidence = DateRepairService.ReadContentEvidence(photo);
            Assert.Equal(4, evidence.Size);
            Assert.False(string.IsNullOrWhiteSpace(evidence.Sha256));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void UndoneTimestampValidationRequiresRestoredTimes()
    {
        var root = NewTempDirectory();
        try
        {
            var photo = Path.Combine(root, "keep.jpg");
            File.WriteAllBytes(photo, [0xFF, 0xD8, 0xFF, 0xD9]);
            var beforeCreation = new DateTime(2026, 9, 13, 6, 57, 59, DateTimeKind.Utc);
            var writeTime = new DateTime(2026, 9, 13, 7, 30, 1, DateTimeKind.Utc);
            File.SetCreationTimeUtc(photo, beforeCreation);
            File.SetLastWriteTimeUtc(photo, writeTime);
            var entry = new DateUndoEntry(
                photo,
                new DateTimeOffset(beforeCreation),
                new DateTimeOffset(writeTime),
                DateTimeOffset.Parse("2026-01-01T06:00:00Z"),
                new DateTimeOffset(writeTime),
                true);

            DateRepairService.ValidateUndoneTimestamps([entry]);

            File.SetCreationTimeUtc(photo, new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc));
            var error = Assert.Throws<IOException>(() =>
                DateRepairService.ValidateUndoneTimestamps([entry]));
            Assert.Contains("creation time was not restored", error.Message, StringComparison.Ordinal);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ScanRejectsDriveRootsBeforeInvokingPowerShell()
    {
        var root = NewTempDirectory();
        try
        {
            var service = new DateRepairService(new PathPolicy(root));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ScanAsync(@"C:\", "artifacts"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void UndoManifestPathResolvesUnderArtifactRoot()
    {
        var root = NewTempDirectory();
        try
        {
            var service = new DateRepairService(new PathPolicy(root));
            var path = service.GetUndoManifestPath("artifacts");
            Assert.Equal(Path.Combine(root, "artifacts", "dates", "date-undo.jsonl"), path);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void AppliedTimestampValidationRequiresProposedCreationTime()
    {
        var root = NewTempDirectory();
        try
        {
            var photo = Path.Combine(root, "keep.jpg");
            File.WriteAllBytes(photo, [0xFF, 0xD8, 0xFF, 0xD9]);
            var proposed = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);
            File.SetCreationTimeUtc(photo, proposed);

            var report = new DateReviewReport
            {
                Policy = "CreationTimeOnly",
                Items =
                [
                    new DateReviewItem
                    {
                        Path = photo,
                        Status = "Proposed",
                        ProposedCaptureTimeUtc = "2026-01-01T06:00:00Z"
                    }
                ]
            };
            var decisions = new[] { new DateDecision(photo, "approve") };

            DateRepairService.ValidateAppliedTimestamps(report, decisions);

            File.SetCreationTimeUtc(photo, new DateTime(2026, 9, 13, 7, 51, 0, DateTimeKind.Utc));
            var error = Assert.Throws<IOException>(() =>
                DateRepairService.ValidateAppliedTimestamps(report, decisions));
            Assert.Contains("creation time was not set to the proposed date", error.Message, StringComparison.Ordinal);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void AppliedTimestampValidationUsesManualDecisionInstant()
    {
        var root = NewTempDirectory();
        try
        {
            var photo = Path.Combine(root, "conflict.jpg");
            File.WriteAllBytes(photo, [0xFF, 0xD8, 0xFF, 0xD9]);
            File.SetCreationTimeUtc(photo, new DateTime(2022, 1, 2, 9, 4, 5, DateTimeKind.Utc));

            var report = new DateReviewReport
            {
                Policy = "CreationTimeOnly",
                Items =
                [
                    new DateReviewItem
                    {
                        Path = photo,
                        Status = "Conflict",
                        ProposedCaptureTimeUtc = "2022-01-02T09:04:05Z"
                    }
                ]
            };
            var decisions = new[] { new DateDecision(photo, "manual", "2022-01-02T09:04:05.0000000Z") };

            DateRepairService.ValidateAppliedTimestamps(report, decisions);

            File.SetCreationTimeUtc(photo, new DateTime(2022, 1, 2, 15, 4, 5, DateTimeKind.Utc));
            var error = Assert.Throws<IOException>(() =>
                DateRepairService.ValidateAppliedTimestamps(report, decisions));
            Assert.Contains("creation time was not set to the proposed date", error.Message, StringComparison.Ordinal);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ApplyRejectsSnapshotForDifferentReviewBeforeProcessInvocation()
    {
        var root = NewTempDirectory();
        try
        {
            var service = new DateRepairService(new PathPolicy(root));
            var snapshot = new DateSnapshot("other-review.json", DateTimeOffset.UtcNow, []);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApplyAsync("requested-review.json", new DateReviewReport(), snapshot, [], "artifacts"));
        }
        finally { Delete(root); }
    }
}

public sealed class DuplicateWorkflowConfigurationSafetyTests : TestBase
{
    [Fact]
    public async Task ConfigureRejectsOverlappingSourceAndQuarantineWithoutRunningScripts()
    {
        var root = NewTempDirectory();
        try
        {
            var policy = new PathPolicy(root);
            var service = new DuplicateWorkflowService(
                new PowerShellScriptRunner(),
                new AtomicArtifactStore(policy),
                policy,
                root);
            var workflow = new WorkflowStateMachine();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ConfigureAsync(workflow, @"\\server\share\photos", @"\\server\share\photos\quarantine", "artifacts"));
            Assert.Equal(WorkflowState.Idle, workflow.Session.State);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ConfigureAcceptsSeparatedRootsAndPersistsConfigWithoutScripts()
    {
        var root = NewTempDirectory();
        try
        {
            var policy = new PathPolicy(root);
            var service = new DuplicateWorkflowService(
                new PowerShellScriptRunner(),
                new AtomicArtifactStore(policy),
                policy,
                root);
            var workflow = new WorkflowStateMachine();
            var config = await service.ConfigureAsync(
                workflow, @"\\server\share\photos", Path.Combine(root, "quarantine"), "artifacts");

            Assert.Equal(WorkflowState.Configured, workflow.Session.State);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "quarantine")), config.QuarantineRoot);
            var configPath = Path.Combine(root, "artifacts", "sessions", workflow.Session.Id.ToString("N"), "config.json");
            Assert.True(File.Exists(configPath));
            Assert.Contains("duplicate-session-config", await File.ReadAllTextAsync(configPath));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ConfigureResetsCompletedAndFailedWorkflows()
    {
        var root = NewTempDirectory();
        try
        {
            var policy = new PathPolicy(root);
            var service = new DuplicateWorkflowService(new PowerShellScriptRunner(), new AtomicArtifactStore(policy), policy, root);
            foreach (var terminal in new[] { WorkflowState.Completed, WorkflowState.Failed })
            {
                var workflow = new WorkflowStateMachine();
                workflow.TransitionTo(terminal == WorkflowState.Completed ? WorkflowState.Configured : WorkflowState.Failed);
                if (terminal == WorkflowState.Completed)
                {
                    workflow.TransitionTo(WorkflowState.Scanning);
                    workflow.TransitionTo(WorkflowState.ScanReady);
                    workflow.TransitionTo(WorkflowState.Reviewing);
                    workflow.TransitionTo(WorkflowState.RemediationReady);
                    workflow.TransitionTo(WorkflowState.RemediationApplied);
                    workflow.TransitionTo(WorkflowState.Completed);
                }
                await service.ConfigureAsync(workflow, @"\\server\share\photos", Path.Combine(root, terminal.ToString()), "artifacts");
                Assert.Equal(WorkflowState.Configured, workflow.Session.State);
            }
        }
        finally { Delete(root); }
    }
}

public sealed class DuplicateReviewValidationTests : TestBase
{
    [Fact]
    public async Task ValidationRequiresEveryGroupResolutionAndIgnoresProtectionOnlyDecisions()
    {
        var root = NewTempDirectory();
        try
        {
            var classified = Path.Combine(root, "classified.json");
            var decisions = Path.Combine(root, "decisions.json");
            var first = Path.Combine(root, "first.jpg");
            var second = Path.Combine(root, "second.jpg");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(second, "second");
            await File.WriteAllTextAsync(classified, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                source = "classifier",
                groups = new[]
                {
                    new
                    {
                        groupId = "g1",
                        suggestedKeepPath = first,
                        items = new[] { new { path = first }, new { path = second } }
                    },
                    new
                    {
                        groupId = "g2",
                        suggestedKeepPath = first,
                        items = new[] { new { path = first } }
                    }
                }
            }));
            await File.WriteAllTextAsync(decisions, JsonSerializer.Serialize(new object[]
            {
                new { groupId = "g1", path = second, action = "protect", @protected = true },
                new { groupId = "g2", action = "defer" }
            }));

            var artifacts = CreateArtifacts(root, classified, decisions);
            var policy = new PathPolicy(root);
            var service = new DuplicateWorkflowService(
                new PowerShellScriptRunner(), new AtomicArtifactStore(policy), policy, root);

            var result = await service.ValidateReviewAsync(artifacts);

            Assert.False(result.IsValid);
            Assert.Equal(1, result.ResolvedGroupCount);
            Assert.Equal(2, result.TotalGroupCount);
            Assert.Equal(["g1"], result.UnresolvedGroupIds);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ValidationAcceptsQuarantineRequestForEveryNonKeeper()
    {
        var root = NewTempDirectory();
        try
        {
            var classified = Path.Combine(root, "classified.json");
            var decisions = Path.Combine(root, "decisions.json");
            var keeper = Path.Combine(root, "keeper.jpg");
            var duplicate = Path.Combine(root, "duplicate.jpg");
            var hash = new string('a', 64);
            await File.WriteAllTextAsync(classified, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                source = "classifier",
                groups = new[]
                {
                    new
                    {
                        groupId = "g1",
                        suggestedKeepPath = keeper,
                        items = new[] { new { path = keeper }, new { path = duplicate } }
                    }
                }
            }));
            await File.WriteAllTextAsync(decisions, JsonSerializer.Serialize(new[]
            {
                new { groupId = "g1", path = duplicate, action = "quarantine-requested", sha256 = hash }
            }));

            var artifacts = CreateArtifacts(root, classified, decisions);
            var policy = new PathPolicy(root);
            var service = new DuplicateWorkflowService(
                new PowerShellScriptRunner(), new AtomicArtifactStore(policy), policy, root);

            var result = await service.ValidateReviewAsync(artifacts);

            Assert.True(result.IsValid);
            Assert.Equal(1, result.ResolvedGroupCount);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ValidationAcceptsTheReviewersSingleDecisionObject()
    {
        var root = NewTempDirectory();
        try
        {
            var classified = Path.Combine(root, "classified.json");
            var decisions = Path.Combine(root, "decisions.json");
            var keeper = Path.Combine(root, "keeper.jpg");
            var duplicate = Path.Combine(root, "duplicate.jpg");
            await File.WriteAllTextAsync(classified, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                source = "classifier",
                groups = new[] { new { groupId = "g1", suggestedKeepPath = keeper, items = new[] { new { path = keeper }, new { path = duplicate } } } }
            }));
            await File.WriteAllTextAsync(decisions, JsonSerializer.Serialize(new { groupId = "g1", action = "keep", keepPath = keeper }));

            var policy = new PathPolicy(root);
            var service = new DuplicateWorkflowService(new PowerShellScriptRunner(), new AtomicArtifactStore(policy), policy, root);
            var result = await service.ValidateReviewAsync(CreateArtifacts(root, classified, decisions));

            Assert.True(result.IsValid);
            Assert.Equal(1, result.ResolvedGroupCount);
        }
        finally { Delete(root); }
    }

    private static DuplicateWorkflowArtifacts CreateArtifacts(string root, string classified, string decisions)
    {
        var normalized = Path.Combine(root, "normalized.json");
        var review = Path.Combine(root, "review.json");
        var html = Path.Combine(root, "review.html");
        var dryRun = Path.Combine(root, "dry-run.json");
        File.WriteAllText(normalized, "{}");
        File.WriteAllText(review, "{}");
        File.WriteAllText(html, string.Empty);
        return new DuplicateWorkflowArtifacts(root, normalized, classified, decisions, review, html, dryRun);
    }
}

public sealed class DuplicateTransactionScanScopeTests : TestBase
{
    [Fact]
    public void ResolveScanIdPrefersClassifiedValueThenScanFolder()
    {
        Assert.Equal("scan-explicit", DuplicateWorkflowService.ResolveScanId(@"C:\reports\other\classified.json", "scan-explicit"));
        Assert.Equal(
            "scan-20260913-120000",
            DuplicateWorkflowService.ResolveScanId(@"C:\reports\czkawka\scan-20260913-120000\classified.json", null));
    }

    [Fact]
    public async Task ReadActiveTransactionsIgnoresOtherScanAndUnscopedLeftovers()
    {
        var root = NewTempDirectory();
        try
        {
            var quarantine = Path.Combine(root, "quarantine");
            Directory.CreateDirectory(quarantine);
            await File.WriteAllLinesAsync(Path.Combine(quarantine, "transactions.jsonl"),
            [
                """{"source":"C:\\leftover.jpg","destination":"C:\\q\\leftover.jpg","transactionUtc":"2026-01-01T00:00:00Z","status":"moved","scanId":"scan-old"}""",
                """{"source":"C:\\unscoped.jpg","destination":"C:\\q\\unscoped.jpg","transactionUtc":"2026-01-02T00:00:00Z","status":"moved"}""",
                """{"source":"C:\\current.jpg","destination":"C:\\q\\current.jpg","transactionUtc":"2026-01-03T00:00:00Z","status":"moved","scanId":"scan-new"}"""
            ]);

            var policy = new PathPolicy(root);
            var service = new DuplicateWorkflowService(
                new PowerShellScriptRunner(), new AtomicArtifactStore(policy), policy, root);
            var config = new AppConfig { QuarantineRoot = quarantine };
            var active = await service.ReadActiveTransactionsAsync(config, "scan-new");

            var current = Assert.Single(active);
            Assert.Equal(@"C:\current.jpg", current.Source);
            Assert.Equal("scan-new", current.ScanId);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ReadScanIdUsesClassifiedDocument()
    {
        var root = NewTempDirectory();
        try
        {
            var classified = Path.Combine(root, "classified.json");
            await File.WriteAllTextAsync(classified, """{"schemaVersion":1,"source":"classifier","scanId":"scan-from-doc","groups":[]}""");
            var policy = new PathPolicy(root);
            var service = new DuplicateWorkflowService(
                new PowerShellScriptRunner(), new AtomicArtifactStore(policy), policy, root);

            Assert.Equal("scan-from-doc", await service.ReadScanIdAsync(classified));
        }
        finally { Delete(root); }
    }
}

public abstract class TestBase
{
    protected static string NewTempDirectory() => TestDirectory.NewTempDirectory();
    protected static void Delete(string path) => TestDirectory.Delete(path);
}

internal static class TestDirectory
{
    internal static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PhotoManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
