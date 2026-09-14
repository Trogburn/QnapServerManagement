using PhotoManager.Models;
using PhotoManager.Services;
using PhotoManager.ViewModels;
using System.Text.Json;
using Xunit;

namespace PhotoManager.Tests;

public sealed class SafetyContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("photos")]
    [InlineData(@"C:")]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"\\server")]
    public void ProductionScanRootRejectsEmptyDriveRootAndIncompleteUnc(string value) =>
        Xunit.Assert.Throws<ArgumentException>(() => PathPolicy.ValidateScanRoot(value));

    [Fact]
    public void ProductionScanRootAcceptsUncChild() =>
        PathPolicy.ValidateScanRoot(@"\\server\share\photos");

    [Fact]
    public void ProductionScanRootAcceptsLocalFolder() =>
        PathPolicy.ValidateScanRoot(@"C:\Photos");

    [Fact]
    public async Task ArtifactStoreRejectsMissingAndTamperedArtifacts()
    {
        var root = NewTempDirectory();
        try
        {
            var store = new AtomicArtifactStore(new PathPolicy(root));
            await Xunit.Assert.ThrowsAnyAsync<IOException>(() =>
                store.ReadAsync<string>("artifacts", "missing.json"));

            var path = await store.WriteAsync("artifacts", "audit.json", "test", "original");
            var original = await File.ReadAllTextAsync(path);
            await File.WriteAllTextAsync(path, original.Replace("original", "changed", StringComparison.Ordinal));
            await Xunit.Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ReadAsync<string>("artifacts", "audit.json"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WorkflowGuardsSnapshotAndTransitions()
    {
        var workflow = new WorkflowStateMachine();
        Xunit.Assert.Throws<InvalidOperationException>(() => workflow.SetSnapshotConfirmed(true));
        workflow.TransitionTo(WorkflowState.Configured);
        workflow.TransitionTo(WorkflowState.Scanning);
        workflow.TransitionTo(WorkflowState.ScanReady);
        workflow.TransitionTo(WorkflowState.Reviewing);
        workflow.TransitionTo(WorkflowState.RemediationReady);
        workflow.SetSnapshotConfirmed(true);
        Xunit.Assert.True(workflow.Session.SnapshotConfirmed);
        workflow.TransitionTo(WorkflowState.RemediationApplied);
        workflow.TransitionTo(WorkflowState.Completed);
        Xunit.Assert.False(workflow.Session.SnapshotConfirmed);
    }

    [Theory]
    [InlineData("Proposed", "2026-01-01T00:00:00Z", true)]
    [InlineData("Conflict", "2022-01-02T09:04:05Z", true)]
    [InlineData("Skipped", "2026-01-01T00:00:00Z", false)]
    [InlineData("Proposed", null, false)]
    [InlineData("Conflict", null, false)]
    public void DateApplyCandidateRequiresProposedEvidence(string status, string? proposed, bool expected)
    {
        var item = new DateReviewItem { Status = status, ProposedCaptureTimeUtc = proposed };
        Xunit.Assert.Equal(expected, DateRepairService.IsApplyCandidate(item));
    }

    [Fact]
    public void DateReviewEvidenceFieldsDeserializeForReviewer()
    {
        const string json = """{"timezonePolicy":"naive values use local time","futureToleranceUtc":"2026-01-02T00:00:00Z","items":[{"path":"\\\\server\\share\\photo.jpg","rawValue":"20260101","parsedFilenameToken":"2026-01-01","timezoneKind":"unspecified-local","timezoneOffset":"-06:00","policy":"CreationTimeOnly","status":"Proposed"}]}""";

        var report = JsonSerializer.Deserialize<DateReviewReport>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Xunit.Assert.NotNull(report);
        Xunit.Assert.Equal("naive values use local time", report!.TimezonePolicy);
        Xunit.Assert.Equal("2026-01-01", report.Items[0].ParsedFilenameToken);
        Xunit.Assert.Equal("-06:00", report.Items[0].TimezoneOffset);
        Xunit.Assert.Equal("CreationTimeOnly", report.Items[0].Policy);
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class LocalDefaultsTests
{
    [Fact]
    public void TryLoadReturnsNullWhenLocalFileIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(LocalDefaults.TryLoad(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryLoadReadsScanAndQuarantineFromLocalOverlay()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "config.local.json"),
                """{"scan":{"uncRoot":"\\\\NAS\\Lab\\Input","quarantineRoot":"\\\\NAS\\Lab\\Quarantine"}}""");

            var defaults = LocalDefaults.TryLoad(root);
            Assert.NotNull(defaults);
            Assert.Equal(@"\\NAS\Lab\Input", defaults!.ScanRoot);
            Assert.Equal(@"\\NAS\Lab\Quarantine", defaults.QuarantineRoot);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MainViewModelKeepsPlaceholdersWithoutLocalDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var viewModel = CreateViewModel(root);
            Assert.Equal(@"\\SERVER\Share\Photos", viewModel.ScanRoot);
            Assert.Equal(@"\\SERVER\Share\PhotoQuarantine", viewModel.QuarantineRoot);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MainViewModelAppliesLocalDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var viewModel = CreateViewModel(
                root,
                new LocalDefaults
                {
                    ScanRoot = @"\\NAS\Lab\Input",
                    QuarantineRoot = @"\\NAS\Lab\Quarantine"
                });
            Assert.Equal(@"\\NAS\Lab\Input", viewModel.ScanRoot);
            Assert.Equal(@"\\NAS\Lab\Quarantine", viewModel.QuarantineRoot);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static MainViewModel CreateViewModel(string root, LocalDefaults? defaults = null)
    {
        var policy = new PathPolicy(root);
        var store = new AtomicArtifactStore(policy);
        return new MainViewModel(
            new WorkflowStateMachine(),
            store,
            new DateRepairService(policy),
            new OrientationRepairService(policy),
            new DuplicateWorkflowService(new PowerShellScriptRunner(), store, policy, root),
            new FakeConfirmationService(true),
            defaults);
    }
}
