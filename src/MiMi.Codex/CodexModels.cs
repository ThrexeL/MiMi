namespace MiMi.Codex;

public sealed record CodexClientState(
    bool Connected,
    bool Busy,
    string Status,
    string? ThreadId,
    string? TurnId,
    string? Model,
    string? LastError)
{
    public static CodexClientState Disconnected { get; } = new(false, false, "Disconnected", null, null, null, null);
}

public sealed record CodexTimelineItem(
    string Id,
    string Kind,
    string Title,
    string Detail,
    string Status,
    DateTimeOffset UpdatedUtc);

public sealed record CodexApprovalRequest(
    string RpcId,
    string Method,
    string ThreadId,
    string TurnId,
    string ItemId,
    string Kind,
    string Command,
    string WorkingDirectory,
    string Reason)
{
    public string DisplayCommand => string.IsNullOrWhiteSpace(Command)
        ? (string.IsNullOrWhiteSpace(Reason) ? "Review Codex request" : Reason)
        : Command;
}
