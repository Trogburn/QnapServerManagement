using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoManager.Infrastructure;
using PhotoManager.Models;
using PhotoManager.Services;

namespace PhotoManager.ViewModels;

public sealed class DateWorkflowViewModel : ObservableObject
{
    private readonly DateRepairService _dateRepair;
    private readonly IShellWorkflowHost _host;
    private readonly Dictionary<string, RetainedDateDecision> _retainedDecisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pathsReopenedByUndo = new(StringComparer.OrdinalIgnoreCase);
    private string? _dateReportPath;
    private DateReviewReport? _dateReport;
    private DateSnapshot? _dateSnapshot;
    private bool _dateSnapshotConfirmed;
    private string? _dateSnapshotName;
    private DateReviewRowViewModel? _selectedDateItem;
    private ImageSource? _datePreview;
    private string _dateEvidenceSummary = "Select a date proposal to inspect its evidence.";
    private string _datePreviewMessage = string.Empty;
    private bool _dateApplyCompleted;
    private bool _dateWorkStarted;
    private bool _dateScanInProgress;
    private int _sampleCount = 25;

    internal DateWorkflowViewModel(DateRepairService dateRepair, IShellWorkflowHost host)
    {
        _dateRepair = dateRepair ?? throw new ArgumentNullException(nameof(dateRepair));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        StartDateWorkCommand = new RelayCommand(StartDateWork, CanStartDateWork);
        ContinueDateWorkCommand = new RelayCommand(ContinueDateWork, CanContinueDateWork);
        ScanDatesCommand = new RelayCommand(ScanDates, CanScanDates);
        CreateDateSnapshotCommand = new RelayCommand(CreateDateSnapshot, CanCreateDateSnapshot);
        ApplyDatesCommand = new RelayCommand(ApplyDates, CanApplyDates);
        UndoSelectedCommand = new RelayCommand(UndoSelected, CanUndoSelected);
        OpenDateUndoCommand = new RelayCommand(OpenDateUndo, CanOpenDateUndo);
        SelectAllDateUndoCommand = new RelayCommand(SelectAllDateUndo, CanSelectAllDateUndo);
        SampleDateReviewCommand = new RelayCommand(SampleDateReview);
    }

    public RelayCommand StartDateWorkCommand { get; }
    public RelayCommand ContinueDateWorkCommand { get; }
    public RelayCommand ScanDatesCommand { get; }
    public RelayCommand CreateDateSnapshotCommand { get; }
    public RelayCommand ApplyDatesCommand { get; }
    public RelayCommand UndoSelectedCommand { get; }
    public RelayCommand OpenDateUndoCommand { get; }
    public RelayCommand SelectAllDateUndoCommand { get; }
    public RelayCommand SampleDateReviewCommand { get; }

    public ObservableCollection<DateReviewRowViewModel> DateItems { get; } = [];
    public ObservableCollection<DateUndoRowViewModel> UndoItems { get; } = [];
    public ObservableCollection<DateBulkApproveGroup> DateBulkApproveGroups { get; } = [];

    public int SampleCount
    {
        get => _sampleCount;
        set => SetProperty(ref _sampleCount, Math.Clamp(value, 1, 1000));
    }

    public string DateReportPath => _dateReportPath ?? "No date evidence report loaded.";
    public string DateTimezonePolicy => _dateReport?.TimezonePolicy ?? string.Empty;
    public string DateSnapshotName =>
        _dateSnapshotName ?? "Scan date evidence to generate a snapshot name.";

    public DateReviewRowViewModel? SelectedDateItem
    {
        get => _selectedDateItem;
        set
        {
            if (SetProperty(ref _selectedDateItem, value))
            {
                UpdateDateEvidencePreview();
            }
        }
    }

    public ImageSource? DatePreview => _datePreview;
    public string DateEvidenceSummary => _dateEvidenceSummary;
    public string DatePreviewMessage => _datePreviewMessage;

    public bool CanConfirmDateSnapshot =>
        _dateSnapshot is not null
        && !_dateApplyCompleted
        && DateReviewDecisionPolicy.CanCreateSnapshot(DateItems);

    public bool DateSnapshotConfirmed
    {
        get => _dateSnapshotConfirmed;
        set
        {
            if (SetProperty(ref _dateSnapshotConfirmed, value))
            {
                ApplyDatesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DateSummary =>
        _dateReport is null
            ? "Run a read-only evidence scan to begin."
            : $"{DateItems.Count(item => item.IsProposed)} proposed; " +
              $"{DateItems.Count(item => item.IsConflict)} conflict; " +
              $"{DateItems.Count(item => item.IsReviewable && !item.HasDecision)} undecided; " +
              $"{DateItems.Count(item => item.IsReviewable && item.IsApproved)} approved; " +
              $"{DateItems.Count(item => item.IsReviewable && item.IsSkipped)} skipped; " +
              $"{DateItems.Count(item => item.IsAlreadyApplied)} already applied.";

    internal void Reset()
    {
        DateItems.Clear();
        UndoItems.Clear();
        DateBulkApproveGroups.Clear();
        _dateReport = null;
        _dateSnapshot = null;
        _dateReportPath = null;
        _dateSnapshotName = null;
        DateSnapshotConfirmed = false;
        _dateApplyCompleted = false;
        _dateWorkStarted = false;
        _dateScanInProgress = false;
        _retainedDecisions.Clear();
        _pathsReopenedByUndo.Clear();
        SelectedDateItem = null;
        NotifyDateSurfaceChanged();
    }

    internal void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(DateSummary));
        OnPropertyChanged(nameof(CanConfirmDateSnapshot));
        RefreshDateBulkApproveGroups();
        StartDateWorkCommand.RaiseCanExecuteChanged();
        ContinueDateWorkCommand.RaiseCanExecuteChanged();
        ScanDatesCommand.RaiseCanExecuteChanged();
        CreateDateSnapshotCommand.RaiseCanExecuteChanged();
        ApplyDatesCommand.RaiseCanExecuteChanged();
        UndoSelectedCommand.RaiseCanExecuteChanged();
        OpenDateUndoCommand.RaiseCanExecuteChanged();
        SelectAllDateUndoCommand.RaiseCanExecuteChanged();
        foreach (var item in DateItems)
        {
            item.RaiseDecisionCommandStates();
        }
    }

    internal void LoadDateReviewForTests(DateReviewReport report, string reportPath = "review.json")
    {
        _dateReport = report;
        _dateReportPath = reportPath;
        _dateSnapshot = null;
        _dateApplyCompleted = false;
        DateSnapshotConfirmed = false;
        PopulateDateItems(report.Items);
        OnPropertyChanged(nameof(DateSummary));
        OnPropertyChanged(nameof(DateTimezonePolicy));
        _host.RaiseCommandStates();
    }

    internal void MarkDateSnapshotForTests(DateSnapshot snapshot)
    {
        _dateSnapshot = snapshot;
        OnPropertyChanged(nameof(CanConfirmDateSnapshot));
        _host.RaiseCommandStates();
    }

    internal void MarkDateApplyCompletedForTests()
    {
        _dateApplyCompleted = true;
        OnPropertyChanged(nameof(CanConfirmDateSnapshot));
        _host.RaiseCommandStates();
    }

    internal void AddDateUndoForTests(DateUndoEntry entry)
    {
        UndoItems.Add(new DateUndoRowViewModel(entry, _host.RaiseCommandStates));
        _host.RaiseCommandStates();
    }

    internal void ReopenDateCycleAfterUndoForTests()
    {
        ReopenDateCycleAfterUndo();
        _host.RaiseCommandStates();
    }

    internal void RememberUndoneDatePathsForTests(params string[] paths) =>
        RememberUndoneDatePaths(paths);

    internal void ShowDateUndoPageForTests()
    {
        _host.Navigate(WorkflowPage.DateUndo);
        _host.RaiseCommandStates();
    }

    internal void ReturnToDateWorkForTests()
    {
        _host.Navigate(WorkflowPage.DateWork);
        _host.RaiseCommandStates();
    }

    internal void MarkDateScanInProgressForTests()
    {
        _dateScanInProgress = true;
        _host.SetStatus("Scan started. Date evidence is still read-only; this can take several minutes.");
        _host.RaiseCommandStates();
    }

    private async void StartDateWork()
    {
        try
        {
            PathPolicy.ValidateScanRoot(_host.ScanRoot);
            if (_host.Workflow.Session.State == WorkflowState.Idle)
            {
                _host.Workflow.TransitionTo(WorkflowState.Configured, "Date-only configuration accepted.");
            }

            var config = new AppConfig { ScanRoot = _host.ScanRoot, ArtifactRoot = _host.ArtifactRoot };
            await _host.Artifacts.WriteAsync(
                _host.ArtifactRoot,
                Path.Combine("sessions", $"{_host.Workflow.Session.Id:N}", "date-config.json"),
                "date-session-config",
                config);
            _dateWorkStarted = true;
            _host.Navigate(WorkflowPage.DateWork);
            _host.SetStatus("Date workflow configured. Scan is read-only.");
            _host.RefreshSession();
            _host.RaiseCommandStates();
            LoadUndo();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private async void ScanDates() => await ScanDatesAsync();

    private async Task ScanDatesAsync()
    {
        if (_dateScanInProgress)
        {
            return;
        }

        _dateScanInProgress = true;
        _host.SetStatus("Scan started. Date evidence is still read-only; this can take several minutes.");
        _host.RaiseCommandStates();
        try
        {
            PathPolicy.ValidateScanRoot(_host.ScanRoot);
            var result = await _dateRepair.ScanAsync(_host.ScanRoot, _host.ArtifactRoot);
            _dateReportPath = result.ReportPath;
            _dateReport = result.Report;
            _dateSnapshot = null;
            _dateSnapshotName = WorkflowSnapshotName.Create(_host.SessionId, "dates");
            _dateApplyCompleted = false;
            DateSnapshotConfirmed = false;
            PopulateDateItems(result.Report.Items);

            if (_host.Workflow.Session.State is WorkflowState.Configured
                or WorkflowState.ScanReady
                or WorkflowState.Reviewing
                or WorkflowState.DateReviewReady
                or WorkflowState.RotateReviewReady)
            {
                if (_host.Workflow.Session.State == WorkflowState.Configured)
                {
                    _host.Workflow.TransitionTo(WorkflowState.Scanning, "Date evidence scan started.");
                    _host.Workflow.TransitionTo(WorkflowState.ScanReady, "Date evidence scan completed.");
                }
                if (_host.Workflow.Session.State is WorkflowState.ScanReady or WorkflowState.RotateReviewReady)
                {
                    _host.Workflow.TransitionTo(WorkflowState.DateReviewReady, "Date evidence is ready for review.");
                }
                _host.RefreshSession();
            }

            _host.SetStatus("Read-only date evidence scan completed. No media was changed.");
            NotifyDateSurfaceChanged();
            _host.RaiseCommandStates();
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _host.SetStatus(exception.Message);
        }
        finally
        {
            _dateScanInProgress = false;
            _host.RaiseCommandStates();
        }
    }

    private async void CreateDateSnapshot()
    {
        try
        {
            if (_dateReport is null || _dateReportPath is null)
            {
                throw new InvalidOperationException("Run a date evidence scan first.");
            }
            if (DateItems.Where(item => item.IsReviewable).Any(item => !item.HasDecision))
            {
                throw new InvalidOperationException("Decide every proposed and conflict row before creating a snapshot.");
            }

            _dateSnapshot = await _dateRepair.CreateSnapshotAsync(
                _dateReportPath, CreateApprovedDateReport(), _host.ArtifactRoot);
            _dateSnapshotName ??= WorkflowSnapshotName.Create(_host.SessionId, "dates");
            _dateApplyCompleted = false;
            DateSnapshotConfirmed = false;
            _host.SetStatus($"Snapshot created for {_dateSnapshot.Items.Count} proposed file(s). Check the confirmation box before apply.");
            OnPropertyChanged(nameof(DateSnapshotName));
            OnPropertyChanged(nameof(CanConfirmDateSnapshot));
            _host.RaiseCommandStates();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            DateSnapshotConfirmed = false;
            _host.SetStatus(exception.Message);
        }
    }

    private void SampleDateReview()
    {
        if (_dateReport is null)
        {
            _host.SetStatus("Run a date evidence scan first.");
            return;
        }

        PopulateDateItems(_dateReport.Items);
        _host.SetStatus($"Showing all {_dateReport.Items.Count} date-evidence item(s).");
        OnPropertyChanged(nameof(DateSummary));
        _host.RaiseCommandStates();
    }

    private async void ApplyDates()
    {
        try
        {
            if (_dateReport is null || _dateReportPath is null || _dateSnapshot is null)
            {
                throw new InvalidOperationException("Create and confirm a snapshot before applying dates.");
            }
            if (DateItems.Where(item => item.IsReviewable).Any(item => !item.HasDecision))
            {
                throw new InvalidOperationException("Decide every proposed and conflict row before applying.");
            }

            var decisions = DateItems.Select(item => item.ToDecision()).ToArray();
            var result = await _dateRepair.ApplyAsync(
                _dateReportPath, _dateReport, _dateSnapshot, decisions, _host.ArtifactRoot);
            _dateApplyCompleted = true;
            OnPropertyChanged(nameof(CanConfirmDateSnapshot));
            await LoadUndoAsync();
            _host.Navigate(WorkflowPage.DateUndo);
            _host.RaiseCommandStates();
            _host.SetStatus($"Applied {result.AppliedCount} date change(s); verification passed. Select files to undo.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private void LoadUndo() => _ = LoadUndoAsync();

    private async Task LoadUndoAsync()
    {
        try
        {
            var manifestPath = _dateRepair.GetUndoManifestPath(_host.ArtifactRoot);
            var entries = await _dateRepair.ReadActiveUndoEntriesAsync(manifestPath);
            UndoItems.Clear();
            foreach (var entry in entries.Reverse())
            {
                UndoItems.Add(new DateUndoRowViewModel(entry, _host.RaiseCommandStates));
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
            var manifestPath = _dateRepair.GetUndoManifestPath(_host.ArtifactRoot);
            var selected = UndoItems.Where(item => item.IsSelected)
                .Select(item => item.Path)
                .ToArray();
            await _dateRepair.UndoAsync(manifestPath, selected, _host.ArtifactRoot);
            RememberUndoneDatePaths(selected);
            ReopenDateCycleAfterUndo();
            await ScanDatesAsync();
            await LoadUndoAsync();
            _host.Navigate(WorkflowPage.DateWork);
            var reopened = DateItems.Count(item => item.IsReviewable && !item.HasDecision);
            _host.SetStatus(UndoItems.Count == 0
                ? $"Undid {selected.Length} selected date change(s). {reopened} restored file(s) need a new decision."
                : $"Undid {selected.Length} selected date change(s). {UndoItems.Count} still applied. {reopened} restored file(s) need a new decision.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _host.SetStatus(exception.Message);
        }
    }

    private void ReopenDateCycleAfterUndo()
    {
        _dateReport = null;
        _dateReportPath = null;
        _dateSnapshot = null;
        _dateSnapshotName = null;
        _dateApplyCompleted = false;
        DateSnapshotConfirmed = false;
        DateItems.Clear();
        SelectedDateItem = null;
        NotifyDateSurfaceChanged();
        _host.RaiseCommandStates();
    }

    private bool CanChangeDateDecision() =>
        _dateSnapshot is null && !_dateApplyCompleted;

    private IEnumerable<DateReviewRowViewModel> BulkApproveCandidates(
        Func<DateReviewRowViewModel, bool> match) =>
        DateItems.Where(item =>
            item.IsProposed
            && item.CanChangeDecision()
            && !item.IsApproved
            && match(item));

    private void ApproveMatching(Func<DateReviewRowViewModel, bool> match)
    {
        foreach (var item in BulkApproveCandidates(match).ToArray())
        {
            item.Decision = "Approve";
        }
    }

    private void RefreshDateBulkApproveGroups()
    {
        var candidates = BulkApproveCandidates(_ => true).ToArray();
        var groups = new List<DateBulkApproveGroup>();
        foreach (var confidence in candidates
            .Select(item => item.Confidence)
            .Where(confidence => !string.IsNullOrWhiteSpace(confidence))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ConfidenceSortKey)
            .ThenBy(confidence => confidence, StringComparer.OrdinalIgnoreCase))
        {
            var count = candidates.Count(item =>
                item.Confidence.Equals(confidence, StringComparison.OrdinalIgnoreCase));
            var value = confidence;
            groups.Add(new DateBulkApproveGroup(
                $"confidence:{value}",
                $"Approve all {value} ({count})",
                () => ApproveMatching(item =>
                    item.Confidence.Equals(value, StringComparison.OrdinalIgnoreCase))));
        }

        foreach (var kind in candidates
            .Select(item => item.EvidenceKind)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(kind => kind, StringComparer.OrdinalIgnoreCase))
        {
            var value = kind;
            var count = candidates.Count(item =>
                item.EvidenceKind.Equals(value, StringComparison.OrdinalIgnoreCase));
            groups.Add(new DateBulkApproveGroup(
                $"kind:{value}",
                $"Approve all {value} ({count})",
                () => ApproveMatching(item =>
                    item.EvidenceKind.Equals(value, StringComparison.OrdinalIgnoreCase))));
        }

        DateBulkApproveGroups.Clear();
        foreach (var group in groups)
        {
            DateBulkApproveGroups.Add(group);
        }
    }

    private static int ConfidenceSortKey(string confidence) => confidence.ToLowerInvariant() switch
    {
        "high" => 0,
        "medium" => 1,
        "low" => 2,
        "none" => 3,
        _ => 4
    };

    private void ContinueDateWork()
    {
        _host.Navigate(WorkflowPage.DateWork);
        _host.SetStatus("Returned to date work.");
        _host.RaiseCommandStates();
    }

    private bool CanStartDateWork() =>
        !_dateWorkStarted
        && _host.CurrentPage == WorkflowPage.Configuration
        && _host.Workflow.Session.State is WorkflowState.Idle
            or WorkflowState.Configured
            or WorkflowState.ScanReady
            or WorkflowState.Reviewing
            or WorkflowState.DateReviewReady
            or WorkflowState.RotateReviewReady
            or WorkflowState.RemediationApplied
            or WorkflowState.Completed;

    private bool CanContinueDateWork() =>
        _dateWorkStarted
        && _host.CurrentPage == WorkflowPage.Configuration;

    private bool CanScanDates() =>
        _host.CurrentPage == WorkflowPage.DateWork
        && !_dateScanInProgress
        && _dateSnapshot is null
        && !_dateApplyCompleted;

    private bool CanCreateDateSnapshot() =>
        _dateReport is not null
        && _dateSnapshot is null
        && !_dateApplyCompleted
        && DateReviewDecisionPolicy.CanCreateSnapshot(DateItems);

    private bool CanApplyDates() =>
        !_dateApplyCompleted
        && DateReviewDecisionPolicy.CanApply(DateSnapshotConfirmed, _dateSnapshot, DateItems);

    private bool CanUndoSelected() =>
        _host.CurrentPage == WorkflowPage.DateUndo && UndoItems.Any(item => item.IsSelected);

    private bool CanOpenDateUndo() =>
        _host.CurrentPage == WorkflowPage.DateWork && UndoItems.Count > 0;

    private void OpenDateUndo()
    {
        _host.Navigate(WorkflowPage.DateUndo);
        _host.SetStatus($"{UndoItems.Count} applied date change(s) can still be undone.");
        _host.RaiseCommandStates();
    }

    private bool CanSelectAllDateUndo() =>
        _host.CurrentPage == WorkflowPage.DateUndo && UndoItems.Count > 0;

    private void SelectAllDateUndo()
    {
        foreach (var item in UndoItems)
        {
            item.IsSelected = true;
        }
    }

    private void PopulateDateItems(IEnumerable<DateReviewItem> items)
    {
        DateItems.Clear();
        foreach (var item in items)
        {
            var row = new DateReviewRowViewModel(item, OnDateRowDecisionChanged, CanChangeDateDecision);
            RestoreRetainedDecision(row);
            DateItems.Add(row);
        }
        SelectedDateItem = DateItems.FirstOrDefault(item => item.IsReviewable && !item.HasDecision)
            ?? DateItems.FirstOrDefault();
    }

    private void RememberDecision(DateReviewRowViewModel row)
    {
        var path = Path.GetFullPath(row.Path);
        if (row.IsReviewable && row.HasDecision)
        {
            _retainedDecisions[path] = new RetainedDateDecision(
                row.Decision, row.ChosenDate, row.ChosenSourceLabel);
            return;
        }

        _retainedDecisions.Remove(path);
    }

    private void RememberUndoneDatePaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            _pathsReopenedByUndo.Add(fullPath);
            _retainedDecisions.Remove(fullPath);
        }
    }

    private void RestoreRetainedDecision(DateReviewRowViewModel row)
    {
        if (!row.IsReviewable)
        {
            return;
        }

        var path = Path.GetFullPath(row.Path);
        if (_pathsReopenedByUndo.Remove(path))
        {
            _retainedDecisions.Remove(path);
            return;
        }

        if (_retainedDecisions.TryGetValue(path, out var decision))
        {
            row.RestoreDecision(decision.Decision, decision.ChosenDate, decision.ChosenSourceLabel);
        }
    }

    private void OnDateRowDecisionChanged(DateReviewRowViewModel row)
    {
        RememberDecision(row);
        SelectedDateItem = row;
        _host.RaiseCommandStates();
        if (DateReviewDecisionPolicy.CanCreateSnapshot(DateItems))
        {
            _host.SetStatus("Decisions are complete. Create a snapshot before apply.");
        }
        else if (DateReviewDecisionPolicy.IsSkipOnlyReviewComplete(DateItems))
        {
            _host.SetStatus("Skip is recorded. Nothing is approved, so there is nothing to snapshot. Undo a still-applied file or reset when finished.");
        }
    }

    private DateReviewReport CreateApprovedDateReport()
    {
        if (_dateReport is null)
        {
            throw new InvalidOperationException("Run a date evidence scan first.");
        }

        return new DateReviewReport
        {
            SchemaVersion = _dateReport.SchemaVersion,
            GeneratedAtUtc = _dateReport.GeneratedAtUtc,
            DryRun = _dateReport.DryRun,
            Policy = _dateReport.Policy,
            TimezonePolicy = _dateReport.TimezonePolicy,
            FutureToleranceUtc = _dateReport.FutureToleranceUtc,
            Items = DateItems.Where(item => item.IsApproved).Select(item => item.Item).ToList()
        };
    }

    private void UpdateDateEvidencePreview()
    {
        _datePreview = null;
        _datePreviewMessage = string.Empty;
        if (_selectedDateItem is null)
        {
            _dateEvidenceSummary = "Select a date proposal to inspect its evidence.";
        }
        else
        {
            _dateEvidenceSummary = _selectedDateItem.DecisionHeadline;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(_selectedDateItem.Path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                _datePreview = image;
            }
            catch (Exception)
            {
                _datePreviewMessage = "Preview unavailable for this file.";
            }
        }

        OnPropertyChanged(nameof(DatePreview));
        OnPropertyChanged(nameof(DateEvidenceSummary));
        OnPropertyChanged(nameof(DatePreviewMessage));
    }

    private void NotifyDateSurfaceChanged()
    {
        OnPropertyChanged(nameof(DateReportPath));
        OnPropertyChanged(nameof(DateSummary));
        OnPropertyChanged(nameof(DateSnapshotName));
        OnPropertyChanged(nameof(DateTimezonePolicy));
        OnPropertyChanged(nameof(CanConfirmDateSnapshot));
    }
}
