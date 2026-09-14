using PhotoManager.Infrastructure;
using PhotoManager.Models;
using PhotoManager.Services;

namespace PhotoManager.ViewModels;

public sealed class RotateReviewRowViewModel : ObservableObject
{
    private readonly Action<RotateReviewRowViewModel> _decisionChanged;
    private readonly Func<bool> _canChangeDecision;
    private readonly string _scannedStatus;
    private readonly string _scannedReason;
    private readonly string _scannedConfidence;
    private readonly string? _scannedProposedRotation;
    private readonly int? _scannedProposedOrientation;
    private string _decision;

    public RotateReviewRowViewModel(
        OrientationReviewItem item,
        Action<RotateReviewRowViewModel> decisionChanged,
        Func<bool>? canChangeDecision = null)
    {
        Item = item;
        _scannedStatus = item.Status;
        _scannedReason = item.Reason;
        _scannedConfidence = item.Confidence;
        _scannedProposedRotation = item.ProposedRotation;
        _scannedProposedOrientation = item.ProposedOrientation;
        _decisionChanged = decisionChanged;
        _canChangeDecision = canChangeDecision ?? (() => true);
        _decision = IsReviewable ? "Undecided" : "Skip";
        ApproveCommand = new RelayCommand(() => ApplyDecision("Approve"), () => CanChangeDecision() && IsProposed);
        SkipCommand = new RelayCommand(() => ApplyDecision("Skip"), () => CanChangeDecision() && IsReviewable);
    }

    public OrientationReviewItem Item { get; }
    public RelayCommand ApproveCommand { get; }
    public RelayCommand SkipCommand { get; }
    public string Path => Item.Path;
    public string FileName => System.IO.Path.GetFileName(Item.Path);
    public string Status => Item.Status;
    public string Confidence => Item.Confidence;
    public string OrientationDescription => Item.Orientation is null
        ? "No Orientation tag"
        : $"{Item.Orientation} · {Item.OrientationLabel}";
    public string SizeDescription => Item.Size == 0 ? "(unknown)" : $"{Item.Size:N0} bytes";
    public string Dimensions => Item.Width is null || Item.Height is null
        ? "(unknown)"
        : $"{Item.Width} x {Item.Height}";
    public string ProposedRotation => Item.ProposedRotation ?? "Leave stored pixels unchanged.";
    public string Reason => Item.Reason;
    public bool IsProposed => Item.Status.Equals("Proposed", StringComparison.OrdinalIgnoreCase);
    public bool IsReviewable => IsProposed;
    public bool HasDecision => !string.Equals(Decision, "Undecided", StringComparison.OrdinalIgnoreCase);
    public bool IsApproved => string.Equals(Decision, "Approve", StringComparison.OrdinalIgnoreCase);
    public bool IsSkipped => string.Equals(Decision, "Skip", StringComparison.OrdinalIgnoreCase);
    public string DecisionLabel => IsReviewable
        ? HasDecision ? Decision : "Undecided"
        : StatusLabel();
    public string DecisionHeadline => IsProposed
        ? ProposedRotation
        : StatusLabel();
    public string DecisionStatusLine => $"{OrientationDescription} · {DecisionLabel}";

    public int? PreviewOrientation =>
        OrientationRepairService.GetApplyOrientation(Item) ?? 1;

    public bool CanManuallyRotate =>
        CanChangeDecision()
        && string.Equals(Item.DecodeStatus, "renderable", StringComparison.OrdinalIgnoreCase);

    public bool CanResetRotation =>
        CanManuallyRotate
        && (Item.Status != _scannedStatus
            || Item.ProposedOrientation != _scannedProposedOrientation
            || Item.ProposedRotation != _scannedProposedRotation);

    public bool CanChangeDecision() => _canChangeDecision();

    public void ApplyManualOrientation(int orientation)
    {
        if (!CanManuallyRotate || orientation is < 2 or > 8)
        {
            return;
        }

        Item.Status = "Proposed";
        Item.ProposedOrientation = orientation;
        Item.ProposedRotation = OrientationContentAnalyzer.DescribeRotation(orientation);
        Item.Confidence = "Medium";
        Item.Reason = string.Equals(_scannedStatus, "Proposed", StringComparison.OrdinalIgnoreCase)
            ? $"Review override: {OrientationContentAnalyzer.DescribeRotationShort(orientation)}."
            : $"EXIF did not mark this file for rotation; {OrientationContentAnalyzer.DescribeRotationShort(orientation)} was chosen during review.";
        NotifyProposalChanged();
        ApplyDecision("Approve");
        RaiseDecisionCommandStates();
    }

    public void ResetManualOrientation()
    {
        if (!CanChangeDecision())
        {
            return;
        }

        Item.Status = _scannedStatus;
        Item.Reason = _scannedReason;
        Item.Confidence = _scannedConfidence;
        Item.ProposedRotation = _scannedProposedRotation;
        Item.ProposedOrientation = _scannedProposedOrientation;
        NotifyProposalChanged();
        ApplyDecision(IsReviewable ? "Undecided" : "Skip");
        RaiseDecisionCommandStates();
    }

    public void RaiseDecisionCommandStates()
    {
        ApproveCommand.RaiseCanExecuteChanged();
        SkipCommand.RaiseCanExecuteChanged();
    }

    public string Decision
    {
        get => _decision;
        set => ApplyDecision(value);
    }

    private void ApplyDecision(string decision)
    {
        if (!CanChangeDecision())
        {
            return;
        }

        if (SetProperty(ref _decision, decision, nameof(Decision)))
        {
            OnPropertyChanged(nameof(IsApproved));
            OnPropertyChanged(nameof(IsSkipped));
            OnPropertyChanged(nameof(HasDecision));
            OnPropertyChanged(nameof(DecisionLabel));
            OnPropertyChanged(nameof(DecisionStatusLine));
        }

        _decisionChanged(this);
    }

    public OrientationDecision ToDecision()
    {
        if (IsReviewable && !HasDecision)
        {
            throw new InvalidOperationException($"A rotate decision is still required for {Path}.");
        }

        return new OrientationDecision(Path, IsApproved ? "approve" : "skip");
    }

    private void NotifyProposalChanged()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Confidence));
        OnPropertyChanged(nameof(ProposedRotation));
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(IsProposed));
        OnPropertyChanged(nameof(IsReviewable));
        OnPropertyChanged(nameof(DecisionHeadline));
        OnPropertyChanged(nameof(DecisionLabel));
        OnPropertyChanged(nameof(DecisionStatusLine));
        OnPropertyChanged(nameof(PreviewOrientation));
        OnPropertyChanged(nameof(CanResetRotation));
    }

    private string StatusLabel() => Item.Status switch
    {
        "AlreadyUpright" => "Already upright",
        "Unsupported" => "Unsupported type",
        "Unreadable" => "Unreadable image",
        "InvalidOrientation" => "Invalid orientation",
        _ => Item.Status
    };
}
