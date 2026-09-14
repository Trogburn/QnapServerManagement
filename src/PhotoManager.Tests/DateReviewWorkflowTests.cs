using PhotoManager.Models;
using PhotoManager.Services;
using PhotoManager.ViewModels;
using Xunit;

namespace PhotoManager.Tests;

public sealed class DateReviewRowViewModelTests
{
    [Fact]
    public void ProposedItemStartsUndecidedAndMapsApproveAndSkip()
    {
        var row = NewRow("Proposed");

        Assert.True(row.IsProposed);
        Assert.False(row.HasDecision);
        Assert.False(row.IsApproved);
        Assert.Throws<InvalidOperationException>(() => row.ToDecision());

        row.Decision = "Approve";
        Assert.True(row.HasDecision);
        Assert.True(row.IsApproved);
        Assert.Equal("approve", row.ToDecision().Action);

        row.Decision = "Skip";
        Assert.True(row.HasDecision);
        Assert.True(row.IsSkipped);
        Assert.False(row.IsApproved);
        Assert.Equal("skip", row.ToDecision().Action);

        row.ApproveCommand.Execute(null);
        Assert.True(row.IsApproved);
        Assert.Equal("Approve", row.DecisionLabel);
    }

    [Fact]
    public void NonProposedItemDefaultsToSkipAndExposesEvidenceFields()
    {
        var item = new DateReviewItem
        {
            Path = @"\\server\share\photos\2026-01-01_keep.jpg",
            Size = 339,
            Status = "Conflict",
            Confidence = "None",
            Source = "filename",
            RawValue = "2026-01-01",
            ParsedFilenameToken = "2026-01-01",
            TimezoneKind = "unspecified-local",
            TimezoneOffset = "-06:00",
            ProposedCaptureTimeUtc = "2026-01-01T06:00:00.0000000Z",
            CurrentCreationTimeUtc = "2026-09-13T06:29:22Z",
            CurrentLastWriteTimeUtc = "2026-09-13T06:29:22Z",
            Policy = "CreationTimeOnly",
            Reason = "Multiple date sources disagree."
        };
        var row = new DateReviewRowViewModel(item, _ => { });

        Assert.Equal("Undecided", row.Decision);
        Assert.True(row.IsConflict);
        Assert.True(row.IsReviewable);
        Assert.Equal("Dates conflict", row.DecisionLabel);
        Assert.Contains("Dates conflict", row.DecisionStatusLine, StringComparison.Ordinal);
        Assert.Equal("2026-01-01_keep.jpg", row.FileName);
        Assert.Equal("339 bytes", row.SizeDescription);
        Assert.Equal("unspecified-local (-06:00)", row.TimezoneDescription);
        Assert.False(string.IsNullOrWhiteSpace(row.ProposedLocalTime));
        Assert.Throws<InvalidOperationException>(() => row.ToDecision());
        Assert.True(row.SkipCommand.CanExecute(null));
        Assert.False(row.ApproveCommand.CanExecute(null));
    }

    private static DateReviewRowViewModel NewRow(string status) =>
        new(new DateReviewItem
        {
            Path = @"\\server\share\photos\file.jpg",
            Status = status,
            ProposedCaptureTimeUtc = "2026-01-01T00:00:00Z"
        }, _ => { });
}

public sealed class DateReviewDecisionPolicyTests
{
    [Fact]
    public void SnapshotRequiresEveryProposedDecisionAndAtLeastOneApproval()
    {
        var first = Proposed("one.jpg");
        var second = Proposed("two.jpg");

        Assert.False(DateReviewDecisionPolicy.CanCreateSnapshot([first, second]));

        first.Decision = "Approve";
        Assert.False(DateReviewDecisionPolicy.CanCreateSnapshot([first, second]));

        second.Decision = "Skip";
        Assert.True(DateReviewDecisionPolicy.CanCreateSnapshot([first, second]));

        first.Decision = "Skip";
        Assert.False(DateReviewDecisionPolicy.HasApprovedProposal([first, second]));
        Assert.False(DateReviewDecisionPolicy.CanCreateSnapshot([first, second]));
        Assert.True(DateReviewDecisionPolicy.IsSkipOnlyReviewComplete([first, second]));
    }

    [Fact]
    public void SnapshotRequiresConflictSourceChoice()
    {
        var proposed = Proposed("one.jpg");
        var conflict = Conflict("two.jpg");
        proposed.Decision = "Approve";

        Assert.False(DateReviewDecisionPolicy.CanCreateSnapshot([proposed, conflict]));

        conflict.SkipCommand.Execute(null);
        Assert.True(DateReviewDecisionPolicy.CanCreateSnapshot([proposed, conflict]));
    }

    [Fact]
    public void ApplyRequiresConfirmedSnapshotAndCompleteDecisions()
    {
        var row = Proposed("one.jpg");
        row.Decision = "Approve";
        var snapshot = new DateSnapshot("review.json", DateTimeOffset.UtcNow, []);

        Assert.False(DateReviewDecisionPolicy.CanApply(false, snapshot, [row]));
        Assert.False(DateReviewDecisionPolicy.CanApply(true, null, [row]));
        Assert.True(DateReviewDecisionPolicy.CanApply(true, snapshot, [row]));
    }

    [Fact]
    public void EmptyOrNullCollectionsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => DateReviewDecisionPolicy.CanCreateSnapshot(null!));
        Assert.False(DateReviewDecisionPolicy.HasCompleteProposedDecisions([]));
    }

    private static DateReviewRowViewModel Proposed(string name) =>
        new(new DateReviewItem
        {
            Path = @"\\server\share\photos\" + name,
            Status = "Proposed",
            ProposedCaptureTimeUtc = "2026-01-01T00:00:00Z"
        }, _ => { });

    private static DateReviewRowViewModel Conflict(string name) =>
        new(new DateReviewItem
        {
            Path = @"\\server\share\photos\" + name,
            Status = "Conflict",
            Source = "exif-DateTimeOriginal",
            RawValue = "2022:01:02 03:04:05",
            ParsedFilenameToken = "2023-02-03",
            ProposedCaptureTimeUtc = "2022-01-02T09:04:05Z",
            Confidence = "High",
            Reason = "Multiple date sources disagree; no automatic change is allowed.",
            EvidenceComparison =
            [
                new DateEvidenceComparison
                {
                    Label = "EXIF",
                    State = "Has date",
                    Detail = "2022:01:02 03:04:05",
                    Selected = true,
                    Utc = "2022-01-02T09:04:05Z"
                },
                new DateEvidenceComparison
                {
                    Label = "Filename",
                    State = "Has date",
                    Detail = "2023-02-03",
                    Utc = "2023-02-03T06:00:00Z"
                }
            ]
        }, _ => { });
}

public sealed class MainViewModelDateWorkflowTests : TestBase
{
    [Fact]
    public void StartDateWorkMovesToDatePageWithoutDuplicateArtifacts()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));

            Assert.Equal(WorkflowPage.DateWork, viewModel.CurrentPage);
            Assert.Equal(WorkflowState.Configured, Enum.Parse<WorkflowState>(viewModel.WorkflowState));
            Assert.Contains("No duplicate workflow artifacts", viewModel.DuplicateArtifactSummary);
            Assert.True(viewModel.ScanDatesCommand.CanExecute(null));
            Assert.False(viewModel.CreateDateSnapshotCommand.CanExecute(null));
            Assert.False(viewModel.ApplyDatesCommand.CanExecute(null));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DateScanLocksWhileInProgressAndReportsInTheFooter()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));

            Assert.True(viewModel.ScanDatesCommand.CanExecute(null));
            viewModel.MarkDateScanInProgressForTests();

            Assert.False(viewModel.ScanDatesCommand.CanExecute(null));
            Assert.StartsWith("Scan started.", viewModel.StatusMessage);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void StartDateWorkRejectsDriveRoot()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.ScanRoot = @"C:\";
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.StatusMessage.Contains("drive root", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(3)));

            Assert.Equal(WorkflowPage.Configuration, viewModel.CurrentPage);
            Assert.Equal("Idle", viewModel.WorkflowState);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void StartDateWorkAcceptsLocalFolder()
    {
        var root = NewTempDirectory();
        try
        {
            var photos = Path.Combine(root, "photos");
            Directory.CreateDirectory(photos);
            var viewModel = CreateViewModel(root);
            viewModel.ScanRoot = photos;
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));
            Assert.Equal("Configured", viewModel.WorkflowState);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ResetReturnsToConfigurationAndClearsDateReview()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));
            viewModel.LoadDateReviewForTests(CreateReport(@"\\server\share\photos\one.jpg"));

            viewModel.ResetCommand.Execute(null);

            Assert.Equal(WorkflowPage.Configuration, viewModel.CurrentPage);
            Assert.Equal("Idle", viewModel.WorkflowState);
            Assert.Empty(viewModel.DateItems);
            Assert.Null(viewModel.SelectedDateItem);
            Assert.False(viewModel.CanConfirmDateSnapshot);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ReturnToConfigurationLeavesDateWorkWithoutResettingSession()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            Assert.False(viewModel.ReturnToConfigurationCommand.CanExecute(null));
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));

            Assert.True(viewModel.ReturnToConfigurationCommand.CanExecute(null));
            viewModel.ReturnToConfigurationCommand.Execute(null);

            Assert.Equal(WorkflowPage.Configuration, viewModel.CurrentPage);
            Assert.Equal(WorkflowState.Configured, Enum.Parse<WorkflowState>(viewModel.WorkflowState));
            Assert.False(viewModel.StartDateWorkCommand.CanExecute(null));
            Assert.True(viewModel.ContinueDateWorkCommand.CanExecute(null));
            Assert.True(viewModel.ConfigureDuplicatesCommand.CanExecute(null));
            Assert.False(viewModel.ContinueDuplicateWorkCommand.CanExecute(null));

            viewModel.ContinueDateWorkCommand.Execute(null);
            Assert.Equal(WorkflowPage.DateWork, viewModel.CurrentPage);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void StartDateWorkFromReviewingDuplicatesDoesNotRequireReset()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.PrepareReviewingDuplicateSessionForTests();

            Assert.Equal(WorkflowPage.Configuration, viewModel.CurrentPage);
            Assert.Equal(WorkflowState.Reviewing, Enum.Parse<WorkflowState>(viewModel.WorkflowState));
            Assert.False(viewModel.ConfigureDuplicatesCommand.CanExecute(null));
            Assert.True(viewModel.ContinueDuplicateWorkCommand.CanExecute(null));
            Assert.True(viewModel.StartDateWorkCommand.CanExecute(null));

            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));

            Assert.Equal(WorkflowState.Reviewing, Enum.Parse<WorkflowState>(viewModel.WorkflowState));
            Assert.True(viewModel.ScanDatesCommand.CanExecute(null));

            viewModel.ReturnToConfigurationCommand.Execute(null);
            Assert.Equal(WorkflowPage.Configuration, viewModel.CurrentPage);
            Assert.True(viewModel.ContinueDuplicateWorkCommand.CanExecute(null));
            Assert.False(viewModel.StartDateWorkCommand.CanExecute(null));
            Assert.True(viewModel.ContinueDateWorkCommand.CanExecute(null));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ConfigureDuplicatesAfterDateWorkDoesNotRequireReset()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));
            viewModel.ReturnToConfigurationCommand.Execute(null);

            Assert.True(viewModel.ConfigureDuplicatesCommand.CanExecute(null));
            viewModel.ConfigureDuplicatesCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DuplicateWork,
                TimeSpan.FromSeconds(3)));

            Assert.Equal(WorkflowState.Configured, Enum.Parse<WorkflowState>(viewModel.WorkflowState));
            Assert.True(viewModel.ScanDuplicatesCommand.CanExecute(null));

            viewModel.ReturnToConfigurationCommand.Execute(null);
            Assert.False(viewModel.ConfigureDuplicatesCommand.CanExecute(null));
            Assert.True(viewModel.ContinueDuplicateWorkCommand.CanExecute(null));
            Assert.False(viewModel.StartDateWorkCommand.CanExecute(null));
            Assert.True(viewModel.ContinueDateWorkCommand.CanExecute(null));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void StartDateWorkAfterFinishedDuplicateKeepsAppliedState()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.PrepareFinishedDuplicateSessionForTests();

            Assert.Equal(WorkflowPage.Configuration, viewModel.CurrentPage);
            Assert.Equal(WorkflowState.RemediationApplied, Enum.Parse<WorkflowState>(viewModel.WorkflowState));
            Assert.False(viewModel.ConfigureDuplicatesCommand.CanExecute(null));
            Assert.True(viewModel.ContinueDuplicateWorkCommand.CanExecute(null));
            Assert.True(viewModel.StartDateWorkCommand.CanExecute(null));

            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));

            Assert.Equal(WorkflowState.RemediationApplied, Enum.Parse<WorkflowState>(viewModel.WorkflowState));
            Assert.True(viewModel.ScanDatesCommand.CanExecute(null));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DateWorkCommandsFollowScanSnapshotConfirmAndApply()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));

            Assert.True(viewModel.ScanDatesCommand.CanExecute(null));
            Assert.False(viewModel.CreateDateSnapshotCommand.CanExecute(null));
            Assert.False(viewModel.ApplyDatesCommand.CanExecute(null));
            Assert.False(viewModel.CanConfirmDateSnapshot);

            var first = @"\\server\share\photos\one.jpg";
            var second = @"\\server\share\photos\two.jpg";
            viewModel.LoadDateReviewForTests(CreateReport(first, second));
            Assert.True(viewModel.ScanDatesCommand.CanExecute(null));
            Assert.False(viewModel.CreateDateSnapshotCommand.CanExecute(null));
            Assert.True(viewModel.DateItems[0].ApproveCommand.CanExecute(null));

            viewModel.DateItems[0].Decision = "Approve";
            Assert.False(viewModel.CreateDateSnapshotCommand.CanExecute(null));
            viewModel.DateItems[1].SkipCommand.Execute(null);
            Assert.True(viewModel.CreateDateSnapshotCommand.CanExecute(null));
            Assert.True(viewModel.ScanDatesCommand.CanExecute(null));
            Assert.False(viewModel.CanConfirmDateSnapshot);

            viewModel.MarkDateSnapshotForTests(new DateSnapshot("review.json", DateTimeOffset.UtcNow, []));
            Assert.True(viewModel.CanConfirmDateSnapshot);
            Assert.False(viewModel.ScanDatesCommand.CanExecute(null));
            Assert.False(viewModel.CreateDateSnapshotCommand.CanExecute(null));
            Assert.False(viewModel.ApplyDatesCommand.CanExecute(null));
            Assert.False(viewModel.DateItems[0].ApproveCommand.CanExecute(null));
            Assert.False(viewModel.DateItems[0].SkipCommand.CanExecute(null));

            viewModel.DateSnapshotConfirmed = true;
            Assert.True(viewModel.ApplyDatesCommand.CanExecute(null));
            Assert.False(viewModel.CreateDateSnapshotCommand.CanExecute(null));

            viewModel.MarkDateApplyCompletedForTests();
            Assert.False(viewModel.ApplyDatesCommand.CanExecute(null));
            Assert.False(viewModel.CreateDateSnapshotCommand.CanExecute(null));
            Assert.False(viewModel.ScanDatesCommand.CanExecute(null));
            Assert.False(viewModel.CanConfirmDateSnapshot);
            Assert.False(viewModel.DateItems[0].ApproveCommand.CanExecute(null));

            viewModel.ReopenDateCycleAfterUndoForTests();
            Assert.True(viewModel.ScanDatesCommand.CanExecute(null));
            Assert.False(viewModel.CreateDateSnapshotCommand.CanExecute(null));
            Assert.False(viewModel.ApplyDatesCommand.CanExecute(null));
            Assert.False(viewModel.CanConfirmDateSnapshot);
            Assert.Empty(viewModel.DateItems);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void SelectingADateUndoRowEnablesUndoImmediately()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.AddDateUndoForTests(new DateUndoEntry(
                @"\\server\share\photos\keep.jpg",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.Parse("2026-01-01T06:00:00Z"),
                DateTimeOffset.UtcNow,
                true));

            Assert.False(viewModel.UndoSelectedCommand.CanExecute(null));
            viewModel.UndoItems[0].IsSelected = true;
            Assert.False(viewModel.UndoSelectedCommand.CanExecute(null));
            viewModel.ShowDateUndoPageForTests();
            Assert.True(viewModel.UndoSelectedCommand.CanExecute(null));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void RescanAfterUndoReopensOnlyTheUndoneFile()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            var keep = @"\\server\share\photos\2026-01-01_keep.jpg";
            var candidate = @"\\server\share\photos\2026-01-01_candidate.jpg";
            viewModel.LoadDateReviewForTests(CreateReport(keep, candidate));
            viewModel.DateItems[0].Decision = "Approve";
            viewModel.DateItems[1].Decision = "Skip";

            viewModel.RememberUndoneDatePathsForTests(keep);
            var rescan = CreateReport(keep, candidate);
            rescan.Items[1] = new DateReviewItem
            {
                Path = candidate,
                Status = "AlreadyApplied",
                Confidence = "Medium",
                Source = "filename",
                ProposedCaptureTimeUtc = "2026-01-01T06:00:00Z"
            };
            viewModel.LoadDateReviewForTests(rescan);

            Assert.True(viewModel.DateItems[0].IsProposed);
            Assert.False(viewModel.DateItems[0].HasDecision);
            Assert.True(viewModel.DateItems[1].IsAlreadyApplied);
            Assert.False(viewModel.DateItems[1].ApproveCommand.CanExecute(null));

            viewModel.LoadDateReviewForTests(CreateReport(keep, candidate));
            Assert.False(viewModel.DateItems[0].HasDecision);
            Assert.True(viewModel.DateItems[1].IsSkipped);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void SkippingTheOnlyProposalRecordsSkipWithoutEnablingSnapshot()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));
            viewModel.LoadDateReviewForTests(CreateReport(@"\\server\share\photos\keep.jpg"));

            viewModel.DateItems[0].SkipCommand.Execute(null);

            Assert.True(viewModel.DateItems[0].IsSkipped);
            Assert.False(viewModel.CreateDateSnapshotCommand.CanExecute(null));
            Assert.Contains("nothing to snapshot", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DateUndoPageOpensFromReviewAndReturnsAfterUndoNavigation()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.StartDateWorkCommand.Execute(null);
            Assert.True(SpinWait.SpinUntil(
                () => viewModel.CurrentPage == WorkflowPage.DateWork,
                TimeSpan.FromSeconds(3)));
            viewModel.AddDateUndoForTests(new DateUndoEntry(
                @"\\server\share\photos\keep.jpg",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.Parse("2026-01-01T06:00:00Z"),
                DateTimeOffset.UtcNow,
                true));

            Assert.True(viewModel.OpenDateUndoCommand.CanExecute(null));
            viewModel.OpenDateUndoCommand.Execute(null);
            Assert.Equal(WorkflowPage.DateUndo, viewModel.CurrentPage);
            Assert.True(viewModel.SelectAllDateUndoCommand.CanExecute(null));
            viewModel.SelectAllDateUndoCommand.Execute(null);
            Assert.True(viewModel.UndoItems[0].IsSelected);
            Assert.True(viewModel.UndoSelectedCommand.CanExecute(null));

            viewModel.ReturnToDateWorkForTests();
            Assert.Equal(WorkflowPage.DateWork, viewModel.CurrentPage);
            Assert.True(viewModel.OpenDateUndoCommand.CanExecute(null));
            Assert.False(viewModel.UndoSelectedCommand.CanExecute(null));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void BulkApproveGroupsCoverEachConfidenceAndEvidenceKind()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.LoadDateReviewForTests(new DateReviewReport
            {
                Items =
                [
                    new DateReviewItem
                    {
                        Path = @"\\server\share\photos\one.jpg",
                        Status = "Proposed",
                        Confidence = "Medium",
                        Source = "filename",
                        ParsedFilenameToken = "2026-01-01",
                        RawValue = "2026-01-01"
                    },
                    new DateReviewItem
                    {
                        Path = @"\\server\share\photos\two.jpg",
                        Status = "Proposed",
                        Confidence = "High",
                        Source = "exif-DateTimeOriginal",
                        RawValue = "2026:01:01 12:00:00"
                    },
                    new DateReviewItem
                    {
                        Path = @"\\server\share\photos\three.jpg",
                        Status = "Proposed",
                        Confidence = "Low",
                        Source = "folder",
                        RawValue = "2026-01-01"
                    }
                ]
            });

            Assert.Equal("Medium · Filename, no EXIF", viewModel.DateItems[0].ClassifierLabel);
            Assert.Equal("High · EXIF, no filename date", viewModel.DateItems[1].ClassifierLabel);
            Assert.Equal("Low · Folder name", viewModel.DateItems[2].ClassifierLabel);
            Assert.Contains(viewModel.DateBulkApproveGroups, group => group.Label == "Approve all High (1)");
            Assert.Contains(viewModel.DateBulkApproveGroups, group => group.Label == "Approve all Medium (1)");
            Assert.Contains(viewModel.DateBulkApproveGroups, group => group.Label == "Approve all Low (1)");
            Assert.Contains(viewModel.DateBulkApproveGroups, group => group.Label == "Approve all Filename, no EXIF (1)");
            Assert.Contains(viewModel.DateBulkApproveGroups, group => group.Label == "Approve all EXIF, no filename date (1)");
            Assert.Contains(viewModel.DateBulkApproveGroups, group => group.Label == "Approve all Folder name (1)");

            viewModel.DateBulkApproveGroups.Single(group => group.Key == "kind:Filename, no EXIF")
                .ApproveCommand.Execute(null);
            Assert.True(viewModel.DateItems[0].IsApproved);
            Assert.False(viewModel.DateItems[1].IsApproved);

            viewModel.DateBulkApproveGroups.Single(group => group.Key == "confidence:High")
                .ApproveCommand.Execute(null);
            Assert.True(viewModel.DateItems[1].IsApproved);
            Assert.False(viewModel.DateItems[2].IsApproved);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void SelectingADateRowBuildsAnEvidenceSummary()
    {
        var root = NewTempDirectory();
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.LoadDateReviewForTests(CreateReport(@"\\server\share\photos\missing-preview.jpg"));

            Assert.NotNull(viewModel.SelectedDateItem);
            viewModel.SelectedDateItem!.ApproveCommand.Execute(null);
            Assert.True(viewModel.SelectedDateItem.IsApproved);
            Assert.Contains("Filename has a date", viewModel.DateEvidenceSummary, StringComparison.Ordinal);
            Assert.Contains("2026-01-01", viewModel.DateEvidenceSummary, StringComparison.Ordinal);
            Assert.Contains("EXIF does not", viewModel.DateEvidenceSummary, StringComparison.Ordinal);
            Assert.DoesNotContain("Selected filename", viewModel.DateEvidenceSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Preview unavailable for this file.", viewModel.DatePreviewMessage);
        }
        finally { Delete(root); }
    }

    private static MainViewModel CreateViewModel(string root)
    {
        var policy = new PathPolicy(root);
        var store = new AtomicArtifactStore(policy);
        var viewModel = new MainViewModel(
            new WorkflowStateMachine(),
            store,
            new DateRepairService(policy),
            new OrientationRepairService(policy),
            new DuplicateWorkflowService(new PowerShellScriptRunner(), store, policy, root),
            new FakeConfirmationService(true))
        {
            ArtifactRoot = root
        };
        return viewModel;
    }

    private static DateReviewReport CreateReport(params string[] paths) =>
        new()
        {
            TimezonePolicy = "Filesystem timestamps are transfer evidence only.",
            Items = paths.Select(path => new DateReviewItem
            {
                Path = path,
                Size = 339,
                Status = "Proposed",
                Confidence = "Medium",
                Source = "filename",
                RawValue = "2026-01-01",
                ParsedFilenameToken = "2026-01-01",
                TimezoneKind = "unspecified-local",
                TimezoneOffset = "-06:00",
                ProposedCaptureTimeUtc = "2026-01-01T06:00:00Z",
                CurrentCreationTimeUtc = "2026-09-13T06:29:22Z",
                CurrentLastWriteTimeUtc = "2026-09-13T06:29:22Z",
                Policy = "CreationTimeOnly",
                Reason = "Selected filename evidence over filesystem transfer timestamps."
            }).ToList()
        };
}

public sealed class DateEvidencePresentationTests
{
    [Fact]
    public void FilenameProposalHighlightsMissingExifWithoutRepeatingEngineReason()
    {
        var item = new DateReviewItem
        {
            Path = @"\\server\share\photos\2026-01-01_keep.jpg",
            Status = "Proposed",
            Source = "filename",
            RawValue = "2026-01-01",
            ParsedFilenameToken = "2026-01-01",
            CurrentCreationTimeUtc = "2026-09-13T06:29:22Z",
            CurrentLastWriteTimeUtc = "2026-09-13T06:29:22Z",
            Reason = "Selected filename evidence over filesystem transfer timestamps."
        };

        var headline = DateEvidencePresentation.BuildHeadline(item);
        var rows = DateEvidencePresentation.BuildRows(item);

        Assert.Equal("Filename has a date (2026-01-01). EXIF does not.", headline);
        Assert.Equal("Filename, no EXIF", DateEvidencePresentation.EvidenceKind(item));
        Assert.DoesNotContain("Selected filename", headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(rows, row => row.Label == "Filename" && row.IsSelected);
        Assert.Contains(rows, row => row.Label == "EXIF" && row.IsMissing);
        Assert.Contains(rows, row => row.Label == "Filesystem" && row.State == "Transfer only");
    }

    [Fact]
    public void ReportDecisionSummaryIsUsedWhenPresent()
    {
        var item = new DateReviewItem
        {
            Status = "Proposed",
            Source = "filename",
            DecisionSummary = "Filename has a date (2026-01-01). EXIF does not.",
            EvidenceComparison =
            [
                new DateEvidenceComparison { Label = "EXIF", State = "Missing", Detail = "No capture date" },
                new DateEvidenceComparison { Label = "Filename", State = "Has date", Detail = "2026-01-01", Selected = true }
            ]
        };

        Assert.Equal(item.DecisionSummary, DateEvidencePresentation.BuildHeadline(item));
        Assert.Equal(2, DateEvidencePresentation.BuildRows(item).Count);
    }

    [Theory]
    [InlineData("InvalidEvidence", "Ambiguous filename date (month/day/year vs day/month/year): 01-02-2024", "Ambiguous date")]
    [InlineData("InvalidEvidence", "Impossible filename date: 2024-02-30", "Impossible date")]
    [InlineData("InvalidEvidence", "Invalid or ambiguous filename date: 2024-13-40", "Invalid date")]
    [InlineData("FutureDate", "Proposed date exceeds the configured future tolerance.", "Future date")]
    [InlineData("NoEvidence", "No supported capture-date evidence was found.", "No dates")]
    [InlineData("Conflict", "Multiple date sources disagree.", "Dates conflict")]
    [InlineData("AlreadyApplied", "Creation time already matches.", "Already applied")]
    public void StatusLabelIsSpecific(string status, string reason, string expected)
    {
        Assert.Equal(expected, DateEvidencePresentation.StatusLabel(new DateReviewItem
        {
            Status = status,
            Reason = reason
        }));
    }

    [Fact]
    public void ConflictRowCanChooseFilenameAndEmitsManualDecision()
    {
        var row = new DateReviewRowViewModel(new DateReviewItem
        {
            Path = @"\\server\share\photos\2023-02-03_conflict.jpg",
            Status = "Conflict",
            Source = "exif-DateTimeOriginal",
            ProposedCaptureTimeUtc = "2022-01-02T09:04:05Z",
            EvidenceComparison =
            [
                new DateEvidenceComparison
                {
                    Label = "EXIF",
                    State = "Has date",
                    Detail = "2022:01:02 03:04:05",
                    Selected = true,
                    Utc = "2022-01-02T09:04:05Z"
                },
                new DateEvidenceComparison
                {
                    Label = "Filename",
                    State = "Has date",
                    Detail = "2023-02-03",
                    Utc = "2023-02-03T06:00:00Z"
                }
            ]
        }, _ => { });

        var filename = Assert.Single(row.SourceChoices, choice => choice.Label == "Filename");
        filename.ChooseCommand.Execute(null);

        Assert.True(row.IsApproved);
        Assert.Equal("Use Filename", row.DecisionLabel);
        var decision = row.ToDecision();
        Assert.Equal("manual", decision.Action);
        Assert.Equal("2023-02-03T06:00:00Z", decision.Date);
    }

    [Fact]
    public void AlreadyAppliedItemIsVisibleButNotADecision()
    {
        var row = new DateReviewRowViewModel(new DateReviewItem
        {
            Path = @"\\server\share\photos\2026-01-01_candidate.jpg",
            Status = "AlreadyApplied",
            Source = "filename",
            Confidence = "Medium",
            ProposedCaptureTimeUtc = "2026-01-01T06:00:00Z",
            DecisionSummary = "Already applied. Filename date (2026-01-01) already matches the file."
        }, _ => { });

        Assert.False(row.IsProposed);
        Assert.True(row.IsAlreadyApplied);
        Assert.Equal("Already applied", row.DecisionLabel);
        Assert.Contains("Already applied", row.DecisionStatusLine, StringComparison.Ordinal);
        Assert.False(row.ApproveCommand.CanExecute(null));
        Assert.False(row.SkipCommand.CanExecute(null));
        Assert.Equal(
            "Already applied. Filename date (2026-01-01) already matches the file.",
            DateEvidencePresentation.BuildHeadline(row.Item));
    }

    [Fact]
    public void EvidenceKindCoversExifAndFolderSources()
    {
        Assert.Equal("EXIF, no filename date", DateEvidencePresentation.EvidenceKind(new DateReviewItem
        {
            Source = "exif-DateTimeOriginal",
            RawValue = "2026:01:01 12:00:00",
            Status = "Proposed"
        }));
        Assert.Equal("Folder name", DateEvidencePresentation.EvidenceKind(new DateReviewItem
        {
            Source = "folder",
            RawValue = "2026-01-01",
            Status = "Proposed"
        }));
        Assert.Equal("Manual date", DateEvidencePresentation.EvidenceKind(new DateReviewItem
        {
            Source = "manual",
            Status = "Proposed"
        }));
    }
}

public sealed class FakeConfirmationService(bool result) : IConfirmationService
{
    public bool Confirm(string message, string title) => result;
}
