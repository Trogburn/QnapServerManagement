namespace PhotoManager.ViewModels;

internal static class WorkflowSnapshotName
{
    public static string Create(string sessionId, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var prefix = operation switch
        {
            "duplicate" => "DUP",
            "rotate" => "ROT",
            _ => "DT"
        };
        return $"QPM-{prefix}-{sessionId[..8]}-{DateTimeOffset.UtcNow:yyMMdd-HHmmss}";
    }
}
