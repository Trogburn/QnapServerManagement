using PhotoManager.Models;
using PhotoManager.Services;
using PhotoManager.ViewModels;
using Xunit;

namespace PhotoManager.Tests;

public sealed class RotateReviewRowViewModelTests
{
    [Fact]
    public void ProposedItemStartsUndecidedAndMapsApproveAndSkip()
    {
        var row = new RotateReviewRowViewModel(ProposedItem(@"\\server\share\photos\sideways.jpg"), _ => { });

        Assert.True(row.IsProposed);
        Assert.True(row.IsReviewable);
        Assert.False(row.HasDecision);
        Assert.Throws<InvalidOperationException>(() => row.ToDecision());

        row.Decision = "Approve";
        Assert.True(row.IsApproved);
        Assert.Equal("approve", row.ToDecision().Action);

        row.Decision = "Skip";
        Assert.True(row.IsSkipped);
        Assert.Equal("skip", row.ToDecision().Action);
    }

    [Fact]
    public void AlreadyUprightItemIsNotReviewable()
    {
        var row = new RotateReviewRowViewModel(new OrientationReviewItem
        {
            Path = @"\\server\share\photos\upright.jpg",
            Status = "AlreadyUpright",
            Orientation = 1,
            OrientationLabel = "Normal"
        }, _ => { });

        Assert.False(row.IsReviewable);
        Assert.Equal("skip", row.ToDecision().Action);
        Assert.False(row.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public void ApplyCandidateAcceptsContentProposalWhenExifIsNormal()
    {
        Assert.True(OrientationRepairService.IsApplyCandidate(new OrientationReviewItem
        {
            Path = "sideways-pixels.jpg",
            Status = "Proposed",
            Orientation = 1,
            ProposedOrientation = 6
        }));
        Assert.Equal(6, OrientationRepairService.GetApplyOrientation(new OrientationReviewItem
        {
            Path = "sideways-pixels.jpg",
            Status = "Proposed",
            Orientation = 1,
            ProposedOrientation = 6
        }));
        Assert.False(OrientationRepairService.IsApplyCandidate(new OrientationReviewItem
        {
            Path = "b.jpg",
            Status = "AlreadyUpright",
            Orientation = 1
        }));
        Assert.True(OrientationRepairService.IsApplyCandidate(ProposedItem("a.jpg")));
    }

    [Fact]
    public void ManualRotatePromotesAlreadyUprightToProposed()
    {
        var row = new RotateReviewRowViewModel(new OrientationReviewItem
        {
            Path = @"\\server\share\photos\giraffe.jpg",
            Status = "AlreadyUpright",
            Orientation = 1,
            OrientationLabel = "Normal",
            DecodeStatus = "renderable",
            Reason = "EXIF Orientation is Normal; stored pixels are already upright.",
            Confidence = "None"
        }, _ => { });

        Assert.False(row.IsReviewable);
        row.ApplyManualOrientation(8);
        Assert.True(row.IsProposed);
        Assert.True(row.IsApproved);
        Assert.Equal(8, row.Item.ProposedOrientation);
        Assert.Equal("approve", row.ToDecision().Action);

        row.ResetManualOrientation();
        Assert.False(row.IsProposed);
        Assert.Equal("skip", row.ToDecision().Action);
        Assert.Null(row.Item.ProposedOrientation);
    }

    private static OrientationReviewItem ProposedItem(string path) =>
        new()
        {
            Path = path,
            Size = 1200,
            Status = "Proposed",
            Orientation = 6,
            OrientationLabel = "Rotate 90 CW",
            ProposedRotation = "Rotate 90 clockwise so the stored pixels are upright.",
            ProposedOrientation = 6,
            Confidence = "High"
        };
}

public sealed class RotateWorkflowGateTests
{
    [Fact]
    public void SnapshotAndApplyStayDisabledUntilEveryProposedRowIsDecided()
    {
        var root = Path.Combine(Path.GetTempPath(), "rotate-gates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var viewModel = CreateViewModel(root);
            viewModel.LoadRotateReviewForTests(new OrientationReviewReport
            {
                Policy = "BakeExifOrientation",
                Items =
                [
                    Proposed("one.jpg"),
                    Proposed("two.jpg")
                ]
            });

            Assert.False(viewModel.CreateRotateSnapshotCommand.CanExecute(null));
            Assert.False(viewModel.ApplyRotationsCommand.CanExecute(null));

            viewModel.RotateItems[0].Decision = "Approve";
            Assert.False(viewModel.CreateRotateSnapshotCommand.CanExecute(null));

            viewModel.RotateItems[1].Decision = "Skip";
            Assert.True(viewModel.CreateRotateSnapshotCommand.CanExecute(null));

            viewModel.MarkRotateSnapshotForTests(new OrientationSnapshot(
                "orientation-review.json",
                DateTimeOffset.UtcNow,
                [new OrientationSnapshotItem(Path.GetFullPath("one.jpg"), 10, DateTimeOffset.UtcNow, 6)]));
            Assert.True(viewModel.CanConfirmRotateSnapshot);
            Assert.False(viewModel.ApplyRotationsCommand.CanExecute(null));

            viewModel.RotateSnapshotConfirmed = true;
            Assert.True(viewModel.ApplyRotationsCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void StartRotateWorkIsEnabledFromIdleConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), "rotate-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var viewModel = CreateViewModel(root);
            Assert.True(viewModel.StartRotateWorkCommand.CanExecute(null));
            Assert.False(viewModel.ContinueRotateWorkCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static MainViewModel CreateViewModel(string root)
    {
        var policy = new PathPolicy(root);
        var store = new AtomicArtifactStore(policy);
        return new MainViewModel(
            new WorkflowStateMachine(),
            store,
            new DateRepairService(policy),
            new OrientationRepairService(policy),
            new DuplicateWorkflowService(new PowerShellScriptRunner(), store, policy, root),
            new FakeConfirmationService(true))
        {
            ArtifactRoot = root
        };
    }

    private static OrientationReviewItem Proposed(string name) =>
        new()
        {
            Path = name,
            Size = 100,
            Status = "Proposed",
            Orientation = 6,
            CurrentLastWriteTimeUtc = DateTimeOffset.UtcNow.ToString("o")
        };
}

public sealed class RotateStateMachineTests
{
    [Fact]
    public void DateReviewReadyCanStartRotateReview()
    {
        var workflow = new WorkflowStateMachine();
        workflow.TransitionTo(WorkflowState.Configured);
        workflow.TransitionTo(WorkflowState.Scanning);
        workflow.TransitionTo(WorkflowState.ScanReady);
        workflow.TransitionTo(WorkflowState.DateReviewReady);
        workflow.TransitionTo(WorkflowState.RotateReviewReady, "sibling rotate scan");

        Assert.Equal(WorkflowState.RotateReviewReady, workflow.Session.State);
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
}
