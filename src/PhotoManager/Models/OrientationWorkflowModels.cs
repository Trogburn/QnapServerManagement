using System.Text.Json.Serialization;

namespace PhotoManager.Models;

public sealed class OrientationReviewReport
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; init; } = string.Empty;

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; init; }

    [JsonPropertyName("policy")]
    public string Policy { get; init; } = string.Empty;

    [JsonPropertyName("items")]
    public List<OrientationReviewItem> Items { get; init; } = [];
}

public sealed class OrientationReviewItem
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("currentCreationTimeUtc")]
    public string CurrentCreationTimeUtc { get; init; } = string.Empty;

    [JsonPropertyName("currentLastWriteTimeUtc")]
    public string CurrentLastWriteTimeUtc { get; init; } = string.Empty;

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("orientation")]
    public int? Orientation { get; init; }

    [JsonPropertyName("orientationLabel")]
    public string? OrientationLabel { get; init; }

    [JsonPropertyName("proposedOrientation")]
    public int? ProposedOrientation { get; set; }

    [JsonPropertyName("proposedRotation")]
    public string? ProposedRotation { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "None";

    [JsonPropertyName("decodeStatus")]
    public string DecodeStatus { get; init; } = string.Empty;
}

public sealed record OrientationSnapshot(
    string ReviewPath,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<OrientationSnapshotItem> Items);

public sealed record OrientationSnapshotItem(
    string Path,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    int Orientation);

public sealed record OrientationDecision(string Path, string Action);

public sealed record OrientationUndoEntry(
    string Path,
    string BackupPath,
    int OrientationBefore,
    string BeforeSha256,
    string AfterSha256,
    DateTimeOffset BeforeCreationTimeUtc,
    DateTimeOffset BeforeLastWriteTimeUtc,
    bool VerificationPassed);

public sealed record OrientationApplyResult(
    string ReviewPath,
    string DecisionPath,
    string VerificationReportPath,
    string UndoManifestPath,
    int AppliedCount,
    bool VerificationPassed);
