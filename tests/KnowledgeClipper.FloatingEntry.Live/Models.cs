using KnowledgeClipper.Workflow;

internal sealed class LiveManifest
{
    public string SchemaVersion { get; init; } = "knowledge-clipper.floating-entry-live.v1";
    public required string DataRoot { get; init; }
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public required LanguageSettings Settings { get; init; }
    public required ContentConfiguration Configuration { get; init; }
    public string ImagePath { get; set; } = "";
    public string ImageSha256 { get; set; } = "";
    public Dictionary<string, EntryFixture> Fixtures { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CardIdentity> Identities { get; set; } = new(StringComparer.Ordinal);
    public List<LiveAttempt> Attempts { get; set; } = [];
}

internal sealed class EntryFixture
{
    public required string Key { get; init; }
    public required CaptureMaterial Material { get; init; }
    public DateTimeOffset? LaunchStartedAt { get; set; }
    public string? DraftId { get; set; }
    public string? SessionId { get; set; }
    public string? ImageJobId { get; set; }
}

internal sealed class LiveAttempt
{
    public int Number { get; init; }
    public string Mode { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string Outcome { get; set; } = "RUNNING";
    public string DesktopAssembly { get; set; } = "";
    public string DesktopSha256 { get; set; } = "";
    public string WorkflowSha256 { get; set; } = "";
    public string IsolatedDesktop { get; set; } = "";
    public string? Error { get; set; }
    public long WindowSamples { get; set; }
    public List<WindowObservation> Windows { get; set; } = [];
    public List<string> Checks { get; set; } = [];
    public List<StepObservation> Steps { get; set; } = [];
    public List<StatusObservation> Statuses { get; set; } = [];
    public List<HttpObservation> Requests { get; set; } = [];
}

internal sealed record WindowObservation(string Phase, string Name, string Type, bool Visible, DateTimeOffset At);
internal sealed record StatusObservation(string Phase, string Text, DateTimeOffset At);
internal sealed class StepObservation
{
    public required string Key { get; init; }
    public string Entry { get; set; } = "";
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string[] CardIds { get; set; } = [];
}
internal sealed record CardIdentity(string DraftId, ExportDestination Destination, string SourceId, string RootId, string DocumentId,
    string[] ContentIds, string[] ReviewCardIds, string? SeparatorId);
internal sealed class HttpObservation
{
    public string Phase { get; init; } = "";
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Path { get; init; } = "";
    public string Method { get; init; } = "";
    public string Operation { get; init; } = "";
    public int? Status { get; set; }
}
