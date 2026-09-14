using System.Collections.ObjectModel;
using System.Windows.Media;
using PhotoManager.Infrastructure;
using PhotoManager.Models;
using PhotoManager.Services;

namespace PhotoManager.ViewModels;

public sealed class RotateWorkflowViewModel : ObservableObject
{
    private readonly OrientationRepairService _orientationRepair;
    private readonly IShellWorkflowHost _host;
    private string? _reportPath;
    private OrientationReviewReport? _report;
    private OrientationSnapshot? _snapshot;
    private bool _snapshotConfirmed;
    private string? _snapshotName;
    private RotateReviewRowViewModel? _selectedItem;
    private ImageSource? _preview;
    private string _previewMessage = string.Empty;
    private bool _applyCompleted;
    private bool _workStarted;
    private bool _scanInProgress;

    internal RotateWorkflowViewModel(OrientationRepairService orientationRepair, IShellWorkflowHost host)
    {
        _orientationRepair = orientationRepair ?? throw new ArgumentNullException(nameof(orientationRepair));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        StartRotateWorkCommand = new RelayCommand(StartRotateWork, CanStartRotateWork);
        ContinueRotateWorkCommand = new RelayCommand(ContinueRotateWork, CanContinueRotateWork);
        ScanRotationsCommand = new RelayCommand(ScanRotations, CanScanRotations);
        CreateRotateSnapshotCommand = new RelayCommand(CreateRotateSnapshot, CanCreateRotateSnapshot);
        ApplyRotationsCommand = new RelayCommand(ApplyRotations, CanApplyRotations);
        ApproveAllProposedCommand = new RelayCommand(ApproveAllProposed, CanApproveAllProposed);
        RotateSelectedClockwiseCommand = new RelayCommand(() => RotateSelected(6), () => CanRotateSelected(6));
        RotateSelectedCounterClockwiseCommand = new RelayCommand(() => RotateSelected(8), () => CanRotateSelected(8));
        RotateSelected180Command = new RelayCommand(() => RotateSelected(3), () => CanRotateSelected(3));
        ResetSelectedRotationCommand = new RelayCommand(ResetSelectedRotation, CanResetSelectedRotation);
        UndoSelectedCommand = new RelayCommand(UndoSelected, CanUndoSelected);
        OpenRotateUndoCommand = new RelayCommand(OpenRotateUndo, CanOpenRotateUndo);
        SelectAllRotateUndoCommand = new RelayCommand(SelectAllRotateUndo, CanSelectAllRotateUndo);
    }

    public RelayCommand StartRotateWorkCommand { get; }
    public RelayCommand ContinueRotateWorkCommand { get; }
    public RelayCommand ScanRotationsCommand { get; }
    public RelayCommand CreateRotateSnapshotCommand { get; }
    public RelayCommand ApplyRotationsCommand { get; }
    public RelayCommand ApproveAllProposedCommand { get; }
    public RelayCommand RotateSelectedClockwiseCommand { get; }
    public RelayCommand RotateSelectedCounterClockwiseCommand { get; }
    public RelayCommand RotateSelected180Command { get; }
    public RelayCommand ResetSelectedRotationCommand { get; }
    public RelayCommand UndoSelectedCommand { get; }
    public RelayCommand OpenRotateUndoCommand { get; }
    public RelayCommand SelectAllRotateUndoCommand { get; }

    public ObservableCollection<RotateReviewRowViewModel> RotateItems { get; } = [];
    public ObservableCollection<RotateUndoRowViewModel> UndoItems { get; } = [];

    public string RotateReportPath => _reportPath ?? "No orientation report loaded.";
    public string RotateSnapshotName =>
        _snapshotName ?? "Scan orientation to generate a snapshot name.";

    public RotateReviewRowViewModel? SelectedRotateItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                UpdatePreview();
                RotateSelectedClockwiseCommand.RaiseCanExecuteChanged();
                RotateSelectedCounterClockwiseCommand.RaiseCanExecuteChanged();
                RotateSelected180Command.RaiseCanExecuteChanged();
                ResetSelectedRotationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ImageSource? RotatePreview => _preview;
    public string RotatePreviewMessage => _previewMessage;

    public bool CanConfirmRotateSnapshot =>
        _snapshot is not null
        && !_applyCompleted
        && OrientationReviewDecisionPolicy.CanCreateSnapshot(RotateItems);

    public bool RotateSnapshotConfirmed
    {
        get => _snapshotConfirmed;
        set
        {
            if (SetProperty(ref _snapshotConfirmed, value))
            {
                ApplyRotationsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string RotateSummary =>
        _report is null
            ? "Run a read-only orientation scan to begin."
            : $"{RotateItems.Count(item => item.IsProposed)} need rotation; " +
              $"{RotateItems.Count(item => item.IsReviewable && !item.HasDecision)} undecided; " +
              $"{RotateItems.Count(item => item.IsApproved)} approved; " +
              $"{RotateItems.Count(item => item.IsSkipped)} skipped; " +
              $"{RotateItems.Count(item => !item.IsReviewable)} already upright or skipped by type.";

    internal void Reset()
    {
        RotateItems.Clear();
        UndoItems.Clear();
        _report = null;
        _snapshot = null;
        _reportPath = null;
        _snapshotName = null;
        RotateSnapshotConfirmed = false;
        _applyCompleted = false;
        _workStarted = false;
        _scanInProgress = false;
        SelectedRotateItem = null;
        NotifySurfaceChanged();
    }

    internal void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(RotateSummary));
        OnPropertyChanged(nameof(CanConfirmRotateSnapshot));
        StartRotateWorkCommand.RaiseCanExecuteChanged();
        ContinueRotateWorkCommand.RaiseCanExecuteChanged();
        ScanRotationsCommand.RaiseCanExecuteChanged();
        CreateRotateSnapshotCommand.RaiseCanExecuteChanged();
        ApplyRotationsCommand.RaiseCanExecuteChanged();
        ApproveAllProposedCommand.RaiseCanExecuteChanged();
        RotateSelectedClockwiseCommand.RaiseCanExecuteChanged();
        RotateSelectedCounterClockwiseCommand.RaiseCanExecuteChanged();
        RotateSelected180Command.RaiseCanExecuteChanged();
        ResetSelectedRotationCommand.RaiseCanExecuteChanged();
        UndoSelectedCommand.RaiseCanExecuteChanged();
        OpenRotateUndoCommand.RaiseCanExecuteChanged();
        SelectAllRotateUndoCommand.RaiseCanExecuteChanged();
        foreach (var item in RotateItems)
        {
            item.RaiseDecisionCommandStates();
        }
    }

    internal void LoadRotateReviewForTests(OrientationReviewReport report, string reportPath = "orientation-review.json")
    {
        _report = report;
        _reportPath = reportPath;
        _snapshot = null;
        _applyCompleted = false;
        RotateSnapshotConfirmed = false;
        PopulateItems(report.Items);
        OnPropertyChanged(nameof(RotateSummary));
        _host.RaiseCommandStates();
    }

    internal void MarkRotateSnapshotForTests(OrientationSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnPropertyChanged(nameof(CanConfirmRotateSnapshot));
        _host.RaiseCommandStates();
    }

    private async void StartRotateWork()
    {
        try
        {
            PathPolicy.ValidateScanRoot(_host.ScanRoot);
            if (_host.Workflow.Session.State == WorkflowState.Idle)
            {
                _host.Workflow.TransitionTo(WorkflowState.Configured, "Rotate-only configuration accepted.");
            }

            var config = new AppConfig { ScanRoot = _host.ScanRoot, ArtifactRoot = _host.ArtifactRoot };
            await _host.Artifacts.WriteAsync(
                _host.ArtifactRoot,
                Path.Combine("sessions", $"{_host.Workflow.Session.Id:N}", "rotate-config.json"),
                "rotate-session-config",
                config);
            _workStarted = true;
            _host.Navigate(WorkflowPage.RotateWork);
            _host.SetStatus("Rotate workflow configured. Scan is read-only.");
            _host.RefreshSession();
            _host.RaiseCommandStates();
            LoadUndo();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private async void ScanRotations() => await ScanRotationsAsync();

    private async Task ScanRotationsAsync()
    {
        if (_scanInProgress)
        {
            return;
        }

        _scanInProgress = true;
        _host.SetStatus("Scan started. Checking EXIF, then photo content for files marked upright.");
        _host.RaiseCommandStates();
        try
        {
            PathPolicy.ValidateScanRoot(_host.ScanRoot);
            var result = await _orientationRepair.ScanAsync(_host.ScanRoot, _host.ArtifactRoot);
            _reportPath = result.ReportPath;
            _report = result.Report;
            _snapshot = null;
            _snapshotName = WorkflowSnapshotName.Create(_host.SessionId, "rotate");
            _applyCompleted = false;
            RotateSnapshotConfirmed = false;
            PopulateItems(result.Report.Items);
            TransitionAfterScan();
            _host.SetStatus("Read-only orientation scan completed. No media was changed.");
            NotifySurfaceChanged();
            _host.RaiseCommandStates();
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _host.SetStatus(exception.Message);
        }
        finally
        {
            _scanInProgress = false;
            _host.RaiseCommandStates();
        }
    }

    private void TransitionAfterScan()
    {
        var state = _host.Workflow.Session.State;
        if (state is not (WorkflowState.Configured
            or WorkflowState.ScanReady
            or WorkflowState.Reviewing
            or WorkflowState.DateReviewReady
            or WorkflowState.RotateReviewReady))
        {
            return;
        }

        if (state == WorkflowState.Configured)
        {
            _host.Workflow.TransitionTo(WorkflowState.Scanning, "Orientation scan started.");
            _host.Workflow.TransitionTo(WorkflowState.ScanReady, "Orientation scan completed.");
            state = WorkflowState.ScanReady;
        }

        if (state is WorkflowState.ScanReady or WorkflowState.Reviewing or WorkflowState.DateReviewReady)
        {
            _host.Workflow.TransitionTo(WorkflowState.RotateReviewReady, "Orientation evidence is ready for review.");
        }

        _host.RefreshSession();
    }

    private async void CreateRotateSnapshot()
    {
        try
        {
            if (_report is null || _reportPath is null)
            {
                throw new InvalidOperationException("Run an orientation scan first.");
            }
            if (RotateItems.Where(item => item.IsReviewable).Any(item => !item.HasDecision))
            {
                throw new InvalidOperationException("Decide every proposed rotation before creating a snapshot.");
            }

            var approvedReport = new OrientationReviewReport
            {
                SchemaVersion = _report.SchemaVersion,
                GeneratedAtUtc = _report.GeneratedAtUtc,
                DryRun = _report.DryRun,
                Policy = _report.Policy,
                Items = RotateItems.Where(item => item.IsApproved).Select(item => item.Item).ToList()
            };
            _report = new OrientationReviewReport
            {
                SchemaVersion = _report.SchemaVersion,
                GeneratedAtUtc = _report.GeneratedAtUtc,
                DryRun = _report.DryRun,
                Policy = _report.Policy,
                Items = RotateItems.Select(item => item.Item).ToList()
            };
            await _orientationRepair.SaveReportAsync(_reportPath, _report);
            _snapshot = await _orientationRepair.CreateSnapshotAsync(
                _reportPath, approvedReport, _host.ArtifactRoot);
            _snapshotName ??= WorkflowSnapshotName.Create(_host.SessionId, "rotate");
            _applyCompleted = false;
            RotateSnapshotConfirmed = false;
            _host.SetStatus($"Snapshot created for {_snapshot.Items.Count} file(s). Check the confirmation box before apply.");
            OnPropertyChanged(nameof(RotateSnapshotName));
            OnPropertyChanged(nameof(CanConfirmRotateSnapshot));
            _host.RaiseCommandStates();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            RotateSnapshotConfirmed = false;
            _host.SetStatus(exception.Message);
        }
    }

    private async void ApplyRotations()
    {
        try
        {
            if (_report is null || _reportPath is null || _snapshot is null)
            {
                throw new InvalidOperationException("Create and confirm a snapshot before rotating files.");
            }

            var decisions = RotateItems.Select(item => item.ToDecision()).ToArray();
            var result = await _orientationRepair.ApplyAsync(
                _reportPath, _report, _snapshot, decisions, _host.ArtifactRoot);
            _applyCompleted = true;
            OnPropertyChanged(nameof(CanConfirmRotateSnapshot));
            await LoadUndoAsync();
            _host.Navigate(WorkflowPage.RotateUndo);
            _host.RaiseCommandStates();
            _host.SetStatus($"Rotated {result.AppliedCount} file(s); originals were backed up. Select files to undo.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private void ApproveAllProposed()
    {
        foreach (var item in RotateItems.Where(item => item.IsProposed && item.CanChangeDecision()).ToArray())
        {
            item.Decision = "Approve";
        }
    }

    private void LoadUndo() => _ = LoadUndoAsync();

    private async Task LoadUndoAsync()
    {
        try
        {
            var manifestPath = _orientationRepair.GetUndoManifestPath(_host.ArtifactRoot);
            var entries = await _orientationRepair.ReadActiveUndoEntriesAsync(manifestPath);
            UndoItems.Clear();
            foreach (var entry in entries.Reverse())
            {
                UndoItems.Add(new RotateUndoRowViewModel(entry, _host.RaiseCommandStates));
            }
            _host.RaiseCommandStates();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private async void UndoSelected()
    {
        try
        {
            var manifestPath = _orientationRepair.GetUndoManifestPath(_host.ArtifactRoot);
            var selected = UndoItems.Where(item => item.IsSelected)
                .Select(item => item.Path)
                .ToArray();
            await _orientationRepair.UndoAsync(manifestPath, selected, _host.ArtifactRoot);
            _report = null;
            _reportPath = null;
            _snapshot = null;
            _snapshotName = null;
            _applyCompleted = false;
            RotateSnapshotConfirmed = false;
            RotateItems.Clear();
            SelectedRotateItem = null;
            await ScanRotationsAsync();
            await LoadUndoAsync();
            _host.Navigate(WorkflowPage.RotateWork);
            _host.SetStatus($"Restored {selected.Length} original file(s) from backup.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private void ContinueRotateWork()
    {
        _host.Navigate(WorkflowPage.RotateWork);
        _host.SetStatus("Returned to rotate work.");
        _host.RaiseCommandStates();
    }

    private void OpenRotateUndo()
    {
        _host.Navigate(WorkflowPage.RotateUndo);
        _host.SetStatus($"{UndoItems.Count} rotated file(s) can still be restored from backup.");
        _host.RaiseCommandStates();
    }

    private void SelectAllRotateUndo()
    {
        foreach (var item in UndoItems)
        {
            item.IsSelected = true;
        }
    }

    private bool CanStartRotateWork() =>
        !_workStarted
        && _host.CurrentPage == WorkflowPage.Configuration
        && _host.Workflow.Session.State is WorkflowState.Idle
            or WorkflowState.Configured
            or WorkflowState.ScanReady
            or WorkflowState.Reviewing
            or WorkflowState.DateReviewReady
            or WorkflowState.RotateReviewReady
            or WorkflowState.RemediationApplied
            or WorkflowState.Completed;

    private bool CanContinueRotateWork() =>
        _workStarted
        && _host.CurrentPage == WorkflowPage.Configuration;

    private bool CanScanRotations() =>
        _host.CurrentPage == WorkflowPage.RotateWork
        && !_scanInProgress
        && _snapshot is null
        && !_applyCompleted;

    private bool CanCreateRotateSnapshot() =>
        _report is not null
        && _snapshot is null
        && !_applyCompleted
        && OrientationReviewDecisionPolicy.CanCreateSnapshot(RotateItems);

    private bool CanApplyRotations() =>
        !_applyCompleted
        && OrientationReviewDecisionPolicy.CanApply(RotateSnapshotConfirmed, _snapshot, RotateItems);

    private void RotateSelected(int orientation)
    {
        if (SelectedRotateItem is null)
        {
            return;
        }

        SelectedRotateItem.ApplyManualOrientation(orientation);
        UpdatePreview();
        _host.RaiseCommandStates();
    }

    private void ResetSelectedRotation()
    {
        if (SelectedRotateItem is null)
        {
            return;
        }

        SelectedRotateItem.ResetManualOrientation();
        UpdatePreview();
        _host.RaiseCommandStates();
    }

    private bool CanRotateSelected(int orientation) =>
        SelectedRotateItem is not null
        && SelectedRotateItem.CanManuallyRotate
        && SelectedRotateItem.PreviewOrientation != orientation;

    private bool CanResetSelectedRotation() =>
        SelectedRotateItem is not null && SelectedRotateItem.CanResetRotation;

    private bool CanApproveAllProposed() =>
        RotateItems.Any(item => item.IsProposed && item.CanChangeDecision() && !item.IsApproved);

    private bool CanUndoSelected() =>
        _host.CurrentPage == WorkflowPage.RotateUndo && UndoItems.Any(item => item.IsSelected);

    private bool CanOpenRotateUndo() =>
        _host.CurrentPage == WorkflowPage.RotateWork && UndoItems.Count > 0;

    private bool CanSelectAllRotateUndo() =>
        _host.CurrentPage == WorkflowPage.RotateUndo && UndoItems.Count > 0;

    private bool CanChangeDecision() => _snapshot is null && !_applyCompleted;

    private void PopulateItems(IEnumerable<OrientationReviewItem> items)
    {
        RotateItems.Clear();
        foreach (var item in items)
        {
            RotateItems.Add(new RotateReviewRowViewModel(item, _ => RaiseCommandStates(), CanChangeDecision));
        }

        SelectedRotateItem = RotateItems.FirstOrDefault(item => item.IsReviewable) ?? RotateItems.FirstOrDefault();
    }

    private void UpdatePreview()
    {
        _preview = null;
        _previewMessage = string.Empty;
        if (_selectedItem is null)
        {
            OnPropertyChanged(nameof(RotatePreview));
            OnPropertyChanged(nameof(RotatePreviewMessage));
            return;
        }

        try
        {
            _preview = OrientationPreview.TryLoad(_selectedItem.Path, _selectedItem.PreviewOrientation);
            _previewMessage = _preview is null
                ? "Preview unavailable for this file."
                : "Preview shows the photo after the proposed rotation. Apply writes that into the file.";
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or ArgumentException)
        {
            _previewMessage = "Preview unavailable for this file.";
        }

        OnPropertyChanged(nameof(RotatePreview));
        OnPropertyChanged(nameof(RotatePreviewMessage));
    }

    private void NotifySurfaceChanged()
    {
        OnPropertyChanged(nameof(RotateReportPath));
        OnPropertyChanged(nameof(RotateSnapshotName));
        OnPropertyChanged(nameof(RotateSummary));
        OnPropertyChanged(nameof(CanConfirmRotateSnapshot));
        UpdatePreview();
    }
}
