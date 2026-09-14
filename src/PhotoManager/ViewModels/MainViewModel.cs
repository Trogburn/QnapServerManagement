using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PhotoManager.Infrastructure;
using PhotoManager.Models;
using PhotoManager.Services;

namespace PhotoManager.ViewModels;

public sealed class MainViewModel : ObservableObject, IShellWorkflowHost
{
    private readonly WorkflowStateMachine _workflow;
    private readonly AtomicArtifactStore _artifactStore;
    private readonly IConfirmationService _confirmationService;
    private string _scanRoot = @"\\SERVER\Share\Photos";
    private string _artifactRoot = "artifacts";
    private string _quarantineRoot = @"\\SERVER\Share\PhotoQuarantine";
    private string _statusMessage = "Ready to configure a session.";
    private WorkflowPage _currentPage = WorkflowPage.Configuration;

    public MainViewModel(
        WorkflowStateMachine workflow,
        AtomicArtifactStore artifactStore,
        DateRepairService dateRepairService,
        OrientationRepairService orientationRepairService,
        DuplicateWorkflowService duplicateWorkflow,
        IConfirmationService confirmationService,
        LocalDefaults? localDefaults = null)
    {
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _confirmationService = confirmationService ?? throw new ArgumentNullException(nameof(confirmationService));
        ApplyLocalDefaults(localDefaults);
        DateWork = new DateWorkflowViewModel(dateRepairService, this);
        RotateWork = new RotateWorkflowViewModel(orientationRepairService, this);
        DuplicateWork = new DuplicateWorkflowViewModel(duplicateWorkflow, this);
        DateWork.PropertyChanged += ForwardChildPropertyChanged;
        RotateWork.PropertyChanged += ForwardChildPropertyChanged;
        DuplicateWork.PropertyChanged += ForwardChildPropertyChanged;
        StartCommand = new RelayCommand(Start);
        ResetCommand = new RelayCommand(Reset);
        ReturnToConfigurationCommand = new RelayCommand(ReturnToConfiguration, CanReturnToConfiguration);
    }

    public DateWorkflowViewModel DateWork { get; }
    public RotateWorkflowViewModel RotateWork { get; }
    public DuplicateWorkflowViewModel DuplicateWork { get; }

    public string ScanRoot
    {
        get => _scanRoot;
        set => SetProperty(ref _scanRoot, value);
    }

    public string ArtifactRoot
    {
        get => _artifactRoot;
        set => SetProperty(ref _artifactRoot, value);
    }

    public string QuarantineRoot
    {
        get => _quarantineRoot;
        set => SetProperty(ref _quarantineRoot, value);
    }

    public string WorkflowState => _workflow.Session.State.ToString();
    public string SessionId => _workflow.Session.Id.ToString("N");
    public bool IsConfigurationEditable => _workflow.Session.State == Models.WorkflowState.Idle;
    public WorkflowPage CurrentPage => _currentPage;

    public Visibility ConfigurationPageVisibility =>
        _currentPage == WorkflowPage.Configuration ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DuplicateWorkPageVisibility =>
        _currentPage == WorkflowPage.DuplicateWork ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DateWorkPageVisibility =>
        _currentPage == WorkflowPage.DateWork ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DateUndoPageVisibility =>
        _currentPage == WorkflowPage.DateUndo ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RotateWorkPageVisibility =>
        _currentPage == WorkflowPage.RotateWork ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RotateUndoPageVisibility =>
        _currentPage == WorkflowPage.RotateUndo ? Visibility.Visible : Visibility.Collapsed;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand StartCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ReturnToConfigurationCommand { get; }
    public RelayCommand StartDateWorkCommand => DateWork.StartDateWorkCommand;
    public RelayCommand ContinueDateWorkCommand => DateWork.ContinueDateWorkCommand;
    public RelayCommand ScanDatesCommand => DateWork.ScanDatesCommand;
    public RelayCommand CreateDateSnapshotCommand => DateWork.CreateDateSnapshotCommand;
    public RelayCommand ApplyDatesCommand => DateWork.ApplyDatesCommand;
    public RelayCommand UndoSelectedCommand => DateWork.UndoSelectedCommand;
    public RelayCommand OpenDateUndoCommand => DateWork.OpenDateUndoCommand;
    public RelayCommand SelectAllDateUndoCommand => DateWork.SelectAllDateUndoCommand;
    public RelayCommand SampleDateReviewCommand => DateWork.SampleDateReviewCommand;
    public RelayCommand StartRotateWorkCommand => RotateWork.StartRotateWorkCommand;
    public RelayCommand ContinueRotateWorkCommand => RotateWork.ContinueRotateWorkCommand;
    public RelayCommand ScanRotationsCommand => RotateWork.ScanRotationsCommand;
    public RelayCommand CreateRotateSnapshotCommand => RotateWork.CreateRotateSnapshotCommand;
    public RelayCommand ApplyRotationsCommand => RotateWork.ApplyRotationsCommand;
    public RelayCommand ApproveAllProposedRotationsCommand => RotateWork.ApproveAllProposedCommand;
    public RelayCommand RotateSelectedClockwiseCommand => RotateWork.RotateSelectedClockwiseCommand;
    public RelayCommand RotateSelectedCounterClockwiseCommand => RotateWork.RotateSelectedCounterClockwiseCommand;
    public RelayCommand RotateSelected180Command => RotateWork.RotateSelected180Command;
    public RelayCommand ResetSelectedRotationCommand => RotateWork.ResetSelectedRotationCommand;
    public RelayCommand UndoSelectedRotationsCommand => RotateWork.UndoSelectedCommand;
    public RelayCommand OpenRotateUndoCommand => RotateWork.OpenRotateUndoCommand;
    public RelayCommand SelectAllRotateUndoCommand => RotateWork.SelectAllRotateUndoCommand;
    public RelayCommand ConfigureDuplicatesCommand => DuplicateWork.ConfigureDuplicatesCommand;
    public RelayCommand ContinueDuplicateWorkCommand => DuplicateWork.ContinueDuplicateWorkCommand;
    public RelayCommand ScanDuplicatesCommand => DuplicateWork.ScanDuplicatesCommand;
    public RelayCommand OpenDuplicateReviewerCommand => DuplicateWork.OpenDuplicateReviewerCommand;
    public RelayCommand ValidateDuplicateReviewCommand => DuplicateWork.ValidateDuplicateReviewCommand;
    public RelayCommand DuplicateDryRunCommand => DuplicateWork.DuplicateDryRunCommand;
    public RelayCommand CopyDuplicateSnapshotNameCommand => DuplicateWork.CopyDuplicateSnapshotNameCommand;
    public RelayCommand DuplicateApplyCommand => DuplicateWork.DuplicateApplyCommand;
    public RelayCommand DuplicateVerifyCommand => DuplicateWork.DuplicateVerifyCommand;
    public RelayCommand LoadDuplicateUndoCommand => DuplicateWork.LoadDuplicateUndoCommand;
    public RelayCommand SelectAllDuplicateUndoCommand => DuplicateWork.SelectAllDuplicateUndoCommand;
    public RelayCommand UndoSelectedDuplicatesCommand => DuplicateWork.UndoSelectedDuplicatesCommand;
    public RelayCommand UndoAllDuplicatesCommand => DuplicateWork.UndoAllDuplicatesCommand;

    public ObservableCollection<DateReviewRowViewModel> DateItems => DateWork.DateItems;
    public ObservableCollection<DateUndoRowViewModel> UndoItems => DateWork.UndoItems;
    public ObservableCollection<DateBulkApproveGroup> DateBulkApproveGroups => DateWork.DateBulkApproveGroups;
    public ObservableCollection<RotateReviewRowViewModel> RotateItems => RotateWork.RotateItems;
    public ObservableCollection<RotateUndoRowViewModel> RotateUndoItems => RotateWork.UndoItems;
    public ObservableCollection<DuplicateUndoRowViewModel> DuplicateUndoItems => DuplicateWork.DuplicateUndoItems;

    public int SampleCount
    {
        get => DateWork.SampleCount;
        set => DateWork.SampleCount = value;
    }

    public string DateReportPath => DateWork.DateReportPath;
    public string DateTimezonePolicy => DateWork.DateTimezonePolicy;
    public DateReviewRowViewModel? SelectedDateItem
    {
        get => DateWork.SelectedDateItem;
        set => DateWork.SelectedDateItem = value;
    }

    public ImageSource? DatePreview => DateWork.DatePreview;
    public string DateEvidenceSummary => DateWork.DateEvidenceSummary;
    public string DatePreviewMessage => DateWork.DatePreviewMessage;
    public bool CanConfirmDateSnapshot => DateWork.CanConfirmDateSnapshot;
    public bool DateSnapshotConfirmed
    {
        get => DateWork.DateSnapshotConfirmed;
        set => DateWork.DateSnapshotConfirmed = value;
    }

    public string DateSummary => DateWork.DateSummary;
    public string DateSnapshotName => DateWork.DateSnapshotName;

    public RotateReviewRowViewModel? SelectedRotateItem
    {
        get => RotateWork.SelectedRotateItem;
        set => RotateWork.SelectedRotateItem = value;
    }

    public ImageSource? RotatePreview => RotateWork.RotatePreview;
    public string RotatePreviewMessage => RotateWork.RotatePreviewMessage;
    public bool CanConfirmRotateSnapshot => RotateWork.CanConfirmRotateSnapshot;
    public bool RotateSnapshotConfirmed
    {
        get => RotateWork.RotateSnapshotConfirmed;
        set => RotateWork.RotateSnapshotConfirmed = value;
    }

    public string RotateSummary => RotateWork.RotateSummary;
    public string RotateSnapshotName => RotateWork.RotateSnapshotName;
    public string RotateReportPath => RotateWork.RotateReportPath;

    public bool DuplicateSnapshotConfirmed
    {
        get => DuplicateWork.DuplicateSnapshotConfirmed;
        set => DuplicateWork.DuplicateSnapshotConfirmed = value;
    }

    public bool CanConfirmDuplicateSnapshot => DuplicateWork.CanConfirmDuplicateSnapshot;
    public string DuplicateArtifactSummary => DuplicateWork.DuplicateArtifactSummary;
    public bool DuplicateReviewValidated => DuplicateWork.DuplicateReviewValidated;
    public string DuplicateReviewValidationSummary => DuplicateWork.DuplicateReviewValidationSummary;
    public string DuplicateSnapshotName => DuplicateWork.DuplicateSnapshotName;

    WorkflowStateMachine IShellWorkflowHost.Workflow => _workflow;
    AtomicArtifactStore IShellWorkflowHost.Artifacts => _artifactStore;
    IConfirmationService IShellWorkflowHost.Confirmation => _confirmationService;

    void IShellWorkflowHost.SetStatus(string message) => StatusMessage = message;

    void IShellWorkflowHost.Navigate(WorkflowPage page)
    {
        if (_currentPage == page)
        {
            return;
        }

        _currentPage = page;
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(ConfigurationPageVisibility));
        OnPropertyChanged(nameof(DuplicateWorkPageVisibility));
        OnPropertyChanged(nameof(DateWorkPageVisibility));
        OnPropertyChanged(nameof(DateUndoPageVisibility));
        OnPropertyChanged(nameof(RotateWorkPageVisibility));
        OnPropertyChanged(nameof(RotateUndoPageVisibility));
        ReturnToConfigurationCommand.RaiseCanExecuteChanged();
    }

    void IShellWorkflowHost.RefreshSession()
    {
        OnPropertyChanged(nameof(WorkflowState));
        OnPropertyChanged(nameof(SessionId));
        OnPropertyChanged(nameof(IsConfigurationEditable));
        OnPropertyChanged(nameof(CanConfirmDuplicateSnapshot));
        OnPropertyChanged(nameof(DuplicateArtifactSummary));
        OnPropertyChanged(nameof(DuplicateReviewValidated));
        OnPropertyChanged(nameof(DuplicateReviewValidationSummary));
        OnPropertyChanged(nameof(DuplicateSnapshotName));
        OnPropertyChanged(nameof(DateSnapshotName));
        OnPropertyChanged(nameof(DateTimezonePolicy));
        OnPropertyChanged(nameof(CanConfirmDateSnapshot));
        OnPropertyChanged(nameof(RotateSnapshotName));
        OnPropertyChanged(nameof(CanConfirmRotateSnapshot));
    }

    void IShellWorkflowHost.RaiseCommandStates()
    {
        DateWork.RaiseCommandStates();
        RotateWork.RaiseCommandStates();
        DuplicateWork.RaiseCommandStates();
        ReturnToConfigurationCommand.RaiseCanExecuteChanged();
    }

    internal void LoadDateReviewForTests(DateReviewReport report, string reportPath = "review.json") =>
        DateWork.LoadDateReviewForTests(report, reportPath);

    internal void MarkDateSnapshotForTests(DateSnapshot snapshot) =>
        DateWork.MarkDateSnapshotForTests(snapshot);

    internal void MarkDateApplyCompletedForTests() =>
        DateWork.MarkDateApplyCompletedForTests();

    internal void AddDateUndoForTests(DateUndoEntry entry) =>
        DateWork.AddDateUndoForTests(entry);

    internal void ReopenDateCycleAfterUndoForTests() =>
        DateWork.ReopenDateCycleAfterUndoForTests();

    internal void RememberUndoneDatePathsForTests(params string[] paths) =>
        DateWork.RememberUndoneDatePathsForTests(paths);

    internal void ShowDateUndoPageForTests() =>
        DateWork.ShowDateUndoPageForTests();

    internal void ReturnToDateWorkForTests() =>
        DateWork.ReturnToDateWorkForTests();

    internal void MarkDateScanInProgressForTests() =>
        DateWork.MarkDateScanInProgressForTests();

    internal void LoadRotateReviewForTests(OrientationReviewReport report, string reportPath = "orientation-review.json") =>
        RotateWork.LoadRotateReviewForTests(report, reportPath);

    internal void MarkRotateSnapshotForTests(OrientationSnapshot snapshot) =>
        RotateWork.MarkRotateSnapshotForTests(snapshot);

    internal void PrepareReviewingDuplicateSessionForTests()
    {
        _workflow.TransitionTo(Models.WorkflowState.Configured);
        _workflow.TransitionTo(Models.WorkflowState.Scanning);
        _workflow.TransitionTo(Models.WorkflowState.ScanReady);
        _workflow.TransitionTo(Models.WorkflowState.Reviewing);
        DuplicateWork.MarkConfiguredForTests(new AppConfig
        {
            ScanRoot = ScanRoot,
            QuarantineRoot = QuarantineRoot,
            ArtifactRoot = ArtifactRoot
        });
        ((IShellWorkflowHost)this).Navigate(WorkflowPage.Configuration);
        ((IShellWorkflowHost)this).RefreshSession();
        ((IShellWorkflowHost)this).RaiseCommandStates();
    }

    internal void PrepareFinishedDuplicateSessionForTests()
    {
        PrepareReviewingDuplicateSessionForTests();
        _workflow.TransitionTo(Models.WorkflowState.RemediationReady);
        _workflow.TransitionTo(Models.WorkflowState.RemediationApplied);
        ((IShellWorkflowHost)this).Navigate(WorkflowPage.Configuration);
        ((IShellWorkflowHost)this).RefreshSession();
        ((IShellWorkflowHost)this).RaiseCommandStates();
    }

    private async void Start()
    {
        try
        {
            PathPolicy.ValidateScanRoot(ScanRoot);
            var session = _workflow.Session;
            if (session.State == Models.WorkflowState.Idle)
            {
                session = _workflow.TransitionTo(Models.WorkflowState.Configured, "Configuration accepted.");
            }

            var config = new AppConfig
            {
                ScanRoot = ScanRoot,
                ArtifactRoot = ArtifactRoot
            };
            await _artifactStore.WriteAsync(
                ArtifactRoot,
                Path.Combine("sessions", $"{session.Id:N}", "config.json"),
                "session-config",
                config);

            StatusMessage = "Session configured. A scan adapter can now transition the workflow to Scanning.";
            ((IShellWorkflowHost)this).RefreshSession();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
    }

    private bool CanReturnToConfiguration() => _currentPage != WorkflowPage.Configuration;

    private void ReturnToConfiguration()
    {
        ((IShellWorkflowHost)this).Navigate(WorkflowPage.Configuration);
        StatusMessage = "Choose the next workflow.";
        ((IShellWorkflowHost)this).RefreshSession();
        ((IShellWorkflowHost)this).RaiseCommandStates();
    }

    private void Reset()
    {
        _workflow.Reset();
        DateWork.Reset();
        RotateWork.Reset();
        DuplicateWork.Reset();
        ((IShellWorkflowHost)this).Navigate(WorkflowPage.Configuration);
        StatusMessage = "Ready to configure a session.";
        ((IShellWorkflowHost)this).RefreshSession();
        ((IShellWorkflowHost)this).RaiseCommandStates();
    }

    private void ApplyLocalDefaults(LocalDefaults? localDefaults)
    {
        if (localDefaults is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(localDefaults.ScanRoot))
        {
            _scanRoot = localDefaults.ScanRoot;
        }

        if (!string.IsNullOrWhiteSpace(localDefaults.QuarantineRoot))
        {
            _quarantineRoot = localDefaults.QuarantineRoot;
        }

        if (!string.IsNullOrWhiteSpace(localDefaults.ArtifactRoot))
        {
            _artifactRoot = localDefaults.ArtifactRoot;
        }
    }

    private void ForwardChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }
}
