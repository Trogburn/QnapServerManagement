using PhotoManager.ViewModels;

namespace PhotoManager.Services;

public static class OrientationReviewDecisionPolicy
{
    public static bool HasCompleteProposedDecisions(IEnumerable<RotateReviewRowViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var reviewable = items.Where(item => item.IsReviewable).ToArray();
        return reviewable.Length > 0 && reviewable.All(item => item.HasDecision);
    }

    public static bool HasApprovedProposal(IEnumerable<RotateReviewRowViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Any(item => item.IsApproved);
    }

    public static bool CanCreateSnapshot(IEnumerable<RotateReviewRowViewModel> items) =>
        HasCompleteProposedDecisions(items) && HasApprovedProposal(items);

    public static bool CanApply(
        bool snapshotConfirmed,
        object? snapshot,
        IEnumerable<RotateReviewRowViewModel> items) =>
        snapshotConfirmed && snapshot is not null && CanCreateSnapshot(items);
}
