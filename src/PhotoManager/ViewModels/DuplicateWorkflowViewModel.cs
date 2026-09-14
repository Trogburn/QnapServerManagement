using System.Collections.ObjectModel;
using System.Windows;
using PhotoManager.Infrastructure;
using PhotoManager.Models;
using PhotoManager.Services;

namespace PhotoManager.ViewModels;

public sealed class DuplicateWorkflowViewModel : ObservableObject
{
    private readonly DuplicateWorkflowService _duplicates;
    private readonly IShellWorkflowHost _host;
    private AppConfig? _duplicateConfig;
    private DuplicateWorkflowArtifacts? _duplicateArtifacts;
    private DuplicateReviewValidationResult? _duplicateReviewValidation;
    private bool _duplicateReviewerOpened;
    private bool _duplicateSnapshotConfirmed;
    private bool _duplicateScanInProgress;
    private string? _duplicateSnapshotName;

    internal DuplicateWorkflowViewModel(DuplicateWorkflowService duplicates, IShellWorkflowHost host)
    {
        _duplicates = duplicates ?? throw new ArgumentNullException(nameof(duplicates));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        ConfigureDuplicatesCommand = new RelayCommand(ConfigureDuplicates, CanConfigureDuplicates);
        ContinueDuplicateWorkCommand = new RelayCommand(ContinueDuplicateWork, CanContinueDuplicateWork);
        ScanDuplicatesCommand = new RelayCommand(ScanDuplicates, CanScanDuplicates);
        OpenDuplicateReviewerCommand = new RelayCommand(OpenDuplicateReviewer, CanOpenDuplicateReviewer);
        ValidateDuplicateReviewCommand = new RelayCommand(ValidateDuplicateReview, CanValidateDuplicateReview);
        DuplicateDryRunCommand = new RelayCommand(DuplicateDryRun, CanDuplicateDryRun);
        CopyDuplicateSnapshotNameCommand = new RelayCommand(CopyDuplicateSnapshotName);
        DuplicateApplyCommand = new RelayCommand(DuplicateApply, CanDuplicateApply);
        DuplicateVerifyCommand = new RelayCommand(DuplicateVerify, CanDuplicateVerify);
        LoadDuplicateUndoCommand = new RelayCommand(LoadDuplicateUndo);
        SelectAllDuplicateUndoCommand = new RelayCommand(SelectAllDuplicateUndo, CanSelectAllDuplicateUndo);
        UndoSelectedDuplicatesCommand = new RelayCommand(UndoSelectedDuplicates, CanUndoSelectedDuplicates);
        UndoAllDuplicatesCommand = new RelayCommand(UndoAllDuplicates, CanUndoAllDuplicates);
    }

    public RelayCommand ConfigureDuplicatesCommand { get; }
    public RelayCommand ContinueDuplicateWorkCommand { get; }
    public RelayCommand ScanDuplicatesCommand { get; }
    public RelayCommand OpenDuplicateReviewerCommand { get; }
    public RelayCommand ValidateDuplicateReviewCommand { get; }
    public RelayCommand DuplicateDryRunCommand { get; }
    public RelayCommand CopyDuplicateSnapshotNameCommand { get; }
    public RelayCommand DuplicateApplyCommand { get; }
    public RelayCommand DuplicateVerifyCommand { get; }
    public RelayCommand LoadDuplicateUndoCommand { get; }
    public RelayCommand SelectAllDuplicateUndoCommand { get; }
    public RelayCommand UndoSelectedDuplicatesCommand { get; }
    public RelayCommand UndoAllDuplicatesCommand { get; }

    public ObservableCollection<DuplicateUndoRowViewModel> DuplicateUndoItems { get; } = [];

    public bool DuplicateSnapshotConfirmed
    {
        get => _duplicateSnapshotConfirmed;
        set
        {
            if (SetProperty(ref _duplicateSnapshotConfirmed, value))
            {
                _host.Workflow.SetSnapshotConfirmed(value);
                if (_host.Workflow.Session.State == WorkflowState.RemediationReady)
                {
                    _duplicates.ConfirmSnapshot(_host.Workflow, value);
                }

                DuplicateApplyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanConfirmDuplicateSnapshot =>
        _duplicateArtifacts?.FreezePath is not null
        && _host.Workflow.Session.State == WorkflowState.RemediationReady;

    public string DuplicateArtifactSummary => _duplicateArtifacts is null
        ? "No duplicate workflow artifacts."
        : $"Artifacts ready: {Path.GetFileName(_duplicateArtifacts.ScanDirectory)} · " +
          $"Dry-run plan: {Path.GetFileName(_duplicateArtifacts.DryRunPath)}";

    public bool DuplicateReviewValidated => _duplicateReviewValidation?.IsValid == true;

    public string DuplicateReviewValidationSummary => _duplicateReviewValidation is null
        ? "Review has not been validated."
        : $"{_duplicateReviewValidation.ResolvedGroupCount} of {_duplicateReviewValidation.TotalGroupCount} groups resolved.";

    public string DuplicateSnapshotName =>
        _duplicateSnapshotName ?? "Run a duplicate dry-run to generate a snapshot name.";

    internal void MarkConfiguredForTests(AppConfig config) =>
        _duplicateConfig = config ?? throw new ArgumentNullException(nameof(config));

    internal void Reset()
    {
        _duplicateConfig = null;
        _duplicateArtifacts = null;
        InvalidateDuplicateReview();
        _duplicateSnapshotName = null;
        _duplicateScanInProgress = false;
        DuplicateUndoItems.Clear();
        DuplicateSnapshotConfirmed = false;
        NotifyDuplicateSurfaceChanged();
    }

    internal void RaiseCommandStates()
    {
        ConfigureDuplicatesCommand.RaiseCanExecuteChanged();
        ContinueDuplicateWorkCommand.RaiseCanExecuteChanged();
        ScanDuplicatesCommand.RaiseCanExecuteChanged();
        OpenDuplicateReviewerCommand.RaiseCanExecuteChanged();
        ValidateDuplicateReviewCommand.RaiseCanExecuteChanged();
        DuplicateDryRunCommand.RaiseCanExecuteChanged();
        DuplicateApplyCommand.RaiseCanExecuteChanged();
        DuplicateVerifyCommand.RaiseCanExecuteChanged();
        UndoSelectedDuplicatesCommand.RaiseCanExecuteChanged();
        SelectAllDuplicateUndoCommand.RaiseCanExecuteChanged();
        UndoAllDuplicatesCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanConfirmDuplicateSnapshot));
        NotifyDuplicateSurfaceChanged();
    }

    private async void ConfigureDuplicates()
    {
        try
        {
            _duplicateConfig = await _duplicates.ConfigureAsync(
                _host.Workflow, _host.ScanRoot, _host.QuarantineRoot, _host.ArtifactRoot);
            _host.Navigate(WorkflowPage.DuplicateWork);
            _host.SetStatus("Duplicate workflow configured; scan remains read-only.");
            _host.RefreshSession();
            _host.RaiseCommandStates();
        }
        catch (Exception exception)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private async void ScanDuplicates()
    {
        if (_duplicateScanInProgress)
        {
            return;
        }

        _duplicateScanInProgress = true;
        _host.SetStatus("Scan started. Duplicate scan is still read-only; this can take several minutes.");
        _host.RaiseCommandStates();
        try
        {
            InvalidateDuplicateReview();
            _duplicateConfig ??= await _duplicates.ConfigureAsync(
                _host.Workflow, _host.ScanRoot, _host.QuarantineRoot, _host.ArtifactRoot);
            _duplicateArtifacts = await _duplicates.PrepareAsync(
                _host.Workflow, _duplicateConfig, fresh: false);
            _host.SetStatus("Duplicate scan, classification, and review archive completed.");
            _host.RefreshSession();
            _host.RaiseCommandStates();
        }
        catch (Exception exception)
        {
            _host.SetStatus(exception.Message);
        }
        finally
        {
            _duplicateScanInProgress = false;
            _host.RaiseCommandStates();
        }
    }

    private async void ValidateDuplicateReview()
    {
        try
        {
            if (_duplicateArtifacts is null)
            {
                throw new InvalidOperationException("Run the duplicate scan before validating the review.");
            }

            _duplicateReviewValidation = await _duplicates.ValidateReviewAsync(_duplicateArtifacts);
            _host.SetStatus(_duplicateReviewValidation.TotalGroupCount == 0
                ? "No duplicate groups were found; no remediation is needed."
                : _duplicateReviewValidation.IsValid
                ? $"Duplicate review validated: {_duplicateReviewValidation.ResolvedGroupCount} of {_duplicateReviewValidation.TotalGroupCount} groups resolved."
                : $"Duplicate review incomplete: {_duplicateReviewValidation.ResolvedGroupCount} of {_duplicateReviewValidation.TotalGroupCount} groups resolved. " +
                  $"Unresolved: {string.Join(", ", _duplicateReviewValidation.UnresolvedGroupIds)}.");
            OnPropertyChanged(nameof(DuplicateReviewValidated));
            OnPropertyChanged(nameof(DuplicateReviewValidationSummary));
            _host.RaiseCommandStates();
        }
        catch (Exception exception)
        {
            InvalidateDuplicateReview();
            _host.SetStatus(exception.Message);
        }
    }

    private async void DuplicateDryRun()
    {
        try
        {
            if (_duplicateConfig is null || _duplicateArtifacts is null)
            {
                throw new InvalidOperationException("Configure and scan duplicates before dry-run.");
            }

            _duplicateArtifacts = await _duplicates.RunDryRunAsync(
                _host.Workflow, _duplicateConfig, _duplicateArtifacts);
            _duplicateSnapshotName = WorkflowSnapshotName.Create(_host.SessionId, "duplicate");
            _host.SetStatus("Dry-run completed and artifacts frozen. Confirm the snapshot before Apply.");
            _host.RefreshSession();
            _host.RaiseCommandStates();
        }
        catch (Exception exception)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private void LoadDuplicateUndo() => _ = LoadDuplicateUndoAsync();

    private async Task LoadDuplicateUndoAsync()
    {
        try
        {
            if (_duplicateConfig is null)
            {
                throw new InvalidOperationException("Configure duplicates before loading undo history.");
            }

            if (_duplicateArtifacts is null)
            {
                DuplicateUndoItems.Clear();
                _host.SetStatus("Scan duplicates before loading undo history.");
                _host.RaiseCommandStates();
                return;
            }

            var scanId = await _duplicates.ReadScanIdAsync(_duplicateArtifacts.ClassifiedPath);
            var entries = await _duplicates.ReadActiveTransactionsAsync(_duplicateConfig, scanId);
            DuplicateUndoItems.Clear();
            foreach (var entry in entries)
            {
                DuplicateUndoItems.Add(new DuplicateUndoRowViewModel(entry, _host.RaiseCommandStates));
            }

            _host.SetStatus(entries.Count == 0
                ? "No active duplicate quarantine transactions were found."
                : $"Loaded {entries.Count} active duplicate transaction(s). Select source path(s) to undo.");
            _host.RaiseCommandStates();
        }
        catch (Exception exception)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private void SelectAllDuplicateUndo()
    {
        foreach (var item in DuplicateUndoItems)
        {
            item.IsSelected = true;
        }
    }

    private async void UndoSelectedDuplicates()
    {
        await UndoDuplicatesAsync(
            DuplicateUndoItems.Where(item => item.IsSelected).Select(item => item.Source).ToArray(),
            "selected");
    }

    private async void UndoAllDuplicates()
    {
        await UndoDuplicatesAsync(
            DuplicateUndoItems.Select(item => item.Source).ToArray(),
            "all");
    }

    private async Task UndoDuplicatesAsync(string[] selected, string scope)
    {
        try
        {
            if (_duplicateConfig is null || _duplicateArtifacts is null)
            {
                throw new InvalidOperationException("Configure duplicates and scan before selective undo.");
            }

            if (selected.Length == 0)
            {
                throw new InvalidOperationException("Select at least one duplicate transaction to undo.");
            }

            var prompt = scope == "all"
                ? $"Restore all {selected.Length} quarantined duplicate file(s) to their source paths?"
                : $"Restore {selected.Length} selected duplicate file(s) to their source paths?";
            if (!_host.Confirmation.Confirm(prompt, "Confirm selective duplicate undo"))
            {
                _host.SetStatus("Selective duplicate undo cancelled.");
                return;
            }

            await _duplicates.UndoSelectedAsync(
                _duplicateConfig, _duplicateArtifacts, selected);
            if (_host.Workflow.Session.State == WorkflowState.Completed)
            {
                _host.Workflow.TransitionTo(
                    WorkflowState.RemediationApplied,
                    "Selective undo changed quarantine after verification.");
            }

            await LoadDuplicateUndoAsync();
            _host.RefreshSession();
            _host.RaiseCommandStates();
            _host.SetStatus(
                $"Restored {selected.Length} file(s). Restore verification passed. {DuplicateUndoItems.Count} still in quarantine.");
        }
        catch (Exception exception)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private void OpenDuplicateReviewer()
    {
        try
        {
            if (_duplicateArtifacts is null)
            {
                throw new InvalidOperationException("Run the duplicate scan before opening the reviewer.");
            }

            InvalidateDuplicateReview();
            _duplicates.OpenReviewer(_duplicateArtifacts);
            _duplicateReviewerOpened = true;
            _host.SetStatus("Duplicate reviewer opened. Save all decisions there before running the dry-run.");
            ValidateDuplicateReviewCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private void CopyDuplicateSnapshotName()
    {
        if (_duplicateSnapshotName is null)
        {
            _host.SetStatus("Run a duplicate dry-run before copying a snapshot name.");
            return;
        }

        Clipboard.SetText(_duplicateSnapshotName);
        _host.SetStatus("Suggested snapshot name copied to the clipboard.");
    }

    private async void DuplicateApply()
    {
        try
        {
            if (!DuplicateSnapshotConfirmed)
            {
                throw new InvalidOperationException("Check the explicit snapshot confirmation before Apply.");
            }

            if (_duplicateConfig is null || _duplicateArtifacts is null)
            {
                throw new InvalidOperationException("Run a duplicate dry-run before Apply.");
            }

            if (!_host.Confirmation.Confirm(
                    "Apply will quarantine only explicitly reviewed files from the frozen snapshot. Continue?",
                    "Confirm duplicate apply"))
            {
                _host.SetStatus("Apply cancelled.");
                return;
            }

            var log = await _duplicates.ApplyAsync(
                _host.Workflow, _duplicateConfig, _duplicateArtifacts, DuplicateSnapshotConfirmed);
            _duplicateArtifacts = _host.Workflow.Session.DuplicateArtifacts ?? _duplicateArtifacts;
            _host.RefreshSession();
            _host.RaiseCommandStates();
            await LoadDuplicateUndoAsync();
            _host.SetStatus(
                $"Duplicate apply completed. {DuplicateUndoItems.Count} file(s) ready to undo. Log: {log}");
        }
        catch (Exception exception)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private async void DuplicateVerify()
    {
        try
        {
            if (_duplicateConfig is null || _duplicateArtifacts is null)
            {
                throw new InvalidOperationException("Run duplicate Apply before verification.");
            }

            var report = await _duplicates.VerifyAsync(
                _host.Workflow, _duplicateConfig, _duplicateArtifacts);
            _host.SetStatus($"Quarantine verification passed. {DuplicateUndoItems.Count} file(s) still quarantined.");
            _host.RefreshSession();
            _host.RaiseCommandStates();
        }
        catch (Exception exception)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private void ContinueDuplicateWork()
    {
        _host.Navigate(WorkflowPage.DuplicateWork);
        _host.SetStatus("Returned to duplicate work.");
        _host.RaiseCommandStates();
    }

    private bool CanConfigureDuplicates() =>
        _host.CurrentPage == WorkflowPage.Configuration
        && _duplicateConfig is null
        && _host.Workflow.Session.State is WorkflowState.Idle
            or WorkflowState.Configured
            or WorkflowState.ScanReady
            or WorkflowState.DateReviewReady
            or WorkflowState.RotateReviewReady
            or WorkflowState.Completed
            or WorkflowState.Failed;

    private bool CanContinueDuplicateWork() =>
        _duplicateConfig is not null
        && _host.CurrentPage == WorkflowPage.Configuration;

    private bool CanScanDuplicates() =>
        !_duplicateScanInProgress
        && _host.CurrentPage == WorkflowPage.DuplicateWork
        && _host.Workflow.Session.State is WorkflowState.Configured
            or WorkflowState.DateReviewReady
            or WorkflowState.RotateReviewReady;

    private bool CanDuplicateDryRun() =>
        _duplicateArtifacts is not null
        && DuplicateReviewValidated
        && _duplicateReviewValidation?.TotalGroupCount > 0
        && _host.Workflow.Session.State == WorkflowState.Reviewing;

    private bool CanValidateDuplicateReview() =>
        _duplicateArtifacts is not null
        && _duplicateReviewerOpened
        && !DuplicateReviewValidated
        && _host.Workflow.Session.State == WorkflowState.Reviewing;

    private bool CanDuplicateApply() =>
        DuplicateSnapshotConfirmed
        && _duplicateArtifacts?.FreezePath is not null
        && _host.Workflow.Session.State == WorkflowState.RemediationReady;

    private bool CanOpenDuplicateReviewer() =>
        _duplicateArtifacts is not null
        && !DuplicateReviewValidated
        && _host.Workflow.Session.State == WorkflowState.Reviewing;

    private bool CanDuplicateVerify() =>
        _duplicateArtifacts?.ApplyLogPath is not null
        && File.Exists(_duplicateArtifacts.ApplyLogPath)
        && _host.Workflow.Session.State == WorkflowState.RemediationApplied;

    private bool CanSelectAllDuplicateUndo() => DuplicateUndoItems.Count > 0;

    private bool CanUndoSelectedDuplicates() =>
        _duplicateConfig is not null
        && _duplicateArtifacts is not null
        && DuplicateUndoItems.Any(item => item.IsSelected);

    private bool CanUndoAllDuplicates() =>
        _duplicateConfig is not null
        && _duplicateArtifacts is not null
        && DuplicateUndoItems.Count > 0;

    private void InvalidateDuplicateReview()
    {
        _duplicateReviewValidation = null;
        _duplicateReviewerOpened = false;
        OnPropertyChanged(nameof(DuplicateReviewValidated));
        OnPropertyChanged(nameof(DuplicateReviewValidationSummary));
        DuplicateDryRunCommand.RaiseCanExecuteChanged();
        ValidateDuplicateReviewCommand.RaiseCanExecuteChanged();
    }

    private void NotifyDuplicateSurfaceChanged()
    {
        OnPropertyChanged(nameof(DuplicateArtifactSummary));
        OnPropertyChanged(nameof(DuplicateReviewValidated));
        OnPropertyChanged(nameof(DuplicateReviewValidationSummary));
        OnPropertyChanged(nameof(DuplicateSnapshotName));
        OnPropertyChanged(nameof(CanConfirmDuplicateSnapshot));
    }
}
