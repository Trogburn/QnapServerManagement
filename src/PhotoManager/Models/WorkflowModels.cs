namespace PhotoManager.Models;

public enum WorkflowState
{
    Idle,
    Configured,
    Scanning,
    ScanReady,
    Reviewing,
    DateReviewReady,
    RotateReviewReady,
    RemediationReady,
    RemediationApplied,
    Completed,
    Failed
}

public sealed record DuplicateWorkflowArtifacts(
    string ScanDirectory,
    string NormalizedPath,
    string ClassifiedPath,
    string DecisionPath,
    string ReviewJsonPath,
    string ReviewHtmlPath,
    string DryRunPath,
    string? ApplyLogPath = null,
    string? VerificationJsonPath = null,
    string? FreezePath = null);

public sealed record DuplicateReviewValidationResult(
    int ResolvedGroupCount,
    int TotalGroupCount,
    IReadOnlyList<string> UnresolvedGroupIds)
{
    public bool IsValid => UnresolvedGroupIds.Count == 0 && ResolvedGroupCount == TotalGroupCount;
}

public sealed record ArtifactFreeze(
    string SessionId,
    DateTimeOffset CreatedUtc,
    string ScanRoot,
    string QuarantineRoot,
    IReadOnlyDictionary<string, string> Sha256ByPath);

public sealed record DuplicateTransactionEntry(
    string Source,
    string Destination,
    DateTimeOffset TransactionUtc,
    string Status,
    DuplicateTransactionEvidence? PostMove = null,
    DuplicateTransactionEvidence? PreMove = null,
    string? ScanId = null);

public sealed record DuplicateTransactionEvidence(
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256);

public sealed record WorkflowSession(
    Guid Id,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    WorkflowState State,
    string? ScanArtifactPath = null,
    string? ReviewArtifactPath = null,
    DuplicateWorkflowArtifacts? DuplicateArtifacts = null,
    ArtifactFreeze? Freeze = null,
    bool SnapshotConfirmed = false,
    string? Error = null);

public sealed record WorkflowTransition(
    WorkflowState From,
    WorkflowState To,
    DateTimeOffset AtUtc,
    string? Reason = null);

public sealed record ArtifactEnvelope<T>(
    int SchemaVersion,
    string ArtifactType,
    DateTimeOffset CreatedUtc,
    T Payload,
    string? PayloadSha256 = null);
