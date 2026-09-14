using PhotoManager.Models;

namespace PhotoManager.Services;

public sealed class WorkflowStateMachine
{
    private static readonly IReadOnlyDictionary<WorkflowState, WorkflowState[]> AllowedTransitions =
        new Dictionary<WorkflowState, WorkflowState[]>
        {
            [WorkflowState.Idle] = [WorkflowState.Configured, WorkflowState.Failed],
            [WorkflowState.Configured] = [WorkflowState.Scanning, WorkflowState.Failed],
            [WorkflowState.Scanning] = [WorkflowState.ScanReady, WorkflowState.Failed],
            [WorkflowState.ScanReady] = [WorkflowState.Reviewing, WorkflowState.DateReviewReady, WorkflowState.RotateReviewReady, WorkflowState.Failed],
            [WorkflowState.Reviewing] = [WorkflowState.DateReviewReady, WorkflowState.RotateReviewReady, WorkflowState.RemediationReady, WorkflowState.Failed],
            [WorkflowState.DateReviewReady] = [WorkflowState.Scanning, WorkflowState.Reviewing, WorkflowState.RotateReviewReady, WorkflowState.RemediationReady, WorkflowState.Failed],
            [WorkflowState.RotateReviewReady] = [WorkflowState.Scanning, WorkflowState.Reviewing, WorkflowState.DateReviewReady, WorkflowState.RemediationReady, WorkflowState.Failed],
            [WorkflowState.RemediationReady] = [WorkflowState.RemediationApplied, WorkflowState.Failed],
            [WorkflowState.RemediationApplied] = [WorkflowState.Completed, WorkflowState.Failed],
            [WorkflowState.Completed] = [WorkflowState.Configured, WorkflowState.RemediationApplied],
            [WorkflowState.Failed] = [WorkflowState.Configured]
        };

    private readonly object _sync = new();
    private readonly List<WorkflowTransition> _history = [];
    private WorkflowSession _session = NewSession();

    public WorkflowSession Session
    {
        get
        {
            lock (_sync)
            {
                return _session;
            }
        }
    }

    public IReadOnlyList<WorkflowTransition> History
    {
        get
        {
            lock (_sync)
            {
                return _history.ToArray();
            }
        }
    }

    public WorkflowSession TransitionTo(
        WorkflowState next,
        string? reason = null,
        string? scanArtifactPath = null,
        string? reviewArtifactPath = null)
    {
        lock (_sync)
        {
            if (!AllowedTransitions.TryGetValue(_session.State, out var allowed)
                || !allowed.Contains(next))
            {
                throw new InvalidOperationException(
                    $"Workflow cannot transition from {_session.State} to {next}.");
            }

            var now = DateTimeOffset.UtcNow;
            _history.Add(new WorkflowTransition(_session.State, next, now, reason));
            _session = _session with
            {
                UpdatedUtc = now,
                State = next,
                ScanArtifactPath = scanArtifactPath ?? _session.ScanArtifactPath,
                ReviewArtifactPath = reviewArtifactPath ?? _session.ReviewArtifactPath,
                Error = next == WorkflowState.Failed ? reason : null,
                SnapshotConfirmed = next is WorkflowState.DateReviewReady
                    or WorkflowState.RotateReviewReady
                    or WorkflowState.RemediationReady
                    ? _session.SnapshotConfirmed
                    : false
            };
            return _session;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _session = NewSession();
            _history.Clear();
        }
    }

    public WorkflowSession SetDuplicateArtifacts(DuplicateWorkflowArtifacts artifacts)
    {
        lock (_sync)
        {
            _session = _session with { DuplicateArtifacts = artifacts, UpdatedUtc = DateTimeOffset.UtcNow };
            return _session;
        }
    }

    public WorkflowSession SetSnapshotConfirmed(bool confirmed)
    {
        lock (_sync)
        {
            if (confirmed && _session.State is not WorkflowState.DateReviewReady
                and not WorkflowState.RotateReviewReady
                and not WorkflowState.RemediationReady)
            {
                throw new InvalidOperationException(
                    "A snapshot can only be confirmed while date review or remediation is ready.");
            }

            _session = _session with { SnapshotConfirmed = confirmed, UpdatedUtc = DateTimeOffset.UtcNow };
            return _session;
        }
    }

    private static WorkflowSession NewSession()
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowSession(Guid.NewGuid(), now, now, WorkflowState.Idle);
    }
}
