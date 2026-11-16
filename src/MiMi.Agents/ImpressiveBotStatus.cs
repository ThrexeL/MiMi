using System.IO;

namespace MiMi.Agents;

/// <summary>
/// What MiMi can truthfully say about ImpressiveBot right now.
///
/// ImpressiveBot pushes one status line per bot through <c>MiMi.AgentCli impressivebot-status</c>
/// (see MiMi.AgentCli.ImpressiveBotRelay). Nothing is polled and nothing is guessed: a bot is
/// "live" only while MiMi holds a recent status for it, and the CLI's presence only says the
/// mirror is installed, never that ImpressiveBot is running.
/// </summary>
public enum ImpressiveBotLink
{
    /// <summary>No MiMi.AgentCli.exe found: ImpressiveBot has nowhere to send status.</summary>
    NotInstalled,
    /// <summary>CLI present, but no ImpressiveBot status has arrived yet.</summary>
    Ready,
    /// <summary>Status arrived recently: ImpressiveBot is running and reporting.</summary>
    Live,
    /// <summary>Previously received status is no longer fresh; current activity is unknown.</summary>
    Stale,
}

/// <summary>One bot's live row: who, what it is doing, and how fresh that is.</summary>
public sealed record ImpressiveBotBotStatus(
    string Bot,
    SessionActivity State,
    bool Stale,
    string Summary,
    string Project,
    DateTimeOffset LastEventUtc)
{
    /// <summary>Working right now. A bot waiting for the person is not counted as working,
    /// so "working" and "waiting" never double-count the same bot.</summary>
    public bool IsActive => !Stale && State is SessionActivity.Started or SessionActivity.Working
        or SessionActivity.ExecutingTool;

    /// <summary>Blocked on the person: a question, or an approval that is answered in ImpressiveBot.</summary>
    public bool NeedsInput => !Stale && State is SessionActivity.AwaitingApproval
        or SessionActivity.WaitingForInput;

    public bool Failed => !Stale && State is SessionActivity.Error;
}

/// <summary>The whole picture MiMi shows for ImpressiveBot: link state plus one row per bot.</summary>
public sealed record ImpressiveBotStatus(
    ImpressiveBotLink Link,
    string CliPath,
    IReadOnlyList<ImpressiveBotBotStatus> Bots)
{
    public int ActiveCount => Bots.Count(b => b.IsActive);
    public int WaitingCount => Bots.Count(b => b.NeedsInput);
    public int FailedCount => Bots.Count(b => b.Failed);

    /// <summary>Short state word for a status line, e.g. "live", "idle", "not detected".</summary>
    public string StateLabel => Link switch
    {
        ImpressiveBotLink.NotInstalled => "not installed",
        ImpressiveBotLink.Stale => "status unknown",
        ImpressiveBotLink.Live when FailedCount > 0 => "needs attention",
        ImpressiveBotLink.Live when WaitingCount > 0 => "waiting for you",
        ImpressiveBotLink.Live when ActiveCount > 0 => "working",
        ImpressiveBotLink.Live => "idle",
        _ => "no status yet",
    };

    /// <summary>Green when ImpressiveBot is reporting and nothing needs the person.</summary>
    public bool IsHealthy => Link == ImpressiveBotLink.Live && FailedCount == 0 && WaitingCount == 0;

    /// <summary>One line for the island: "ImpressiveBot · working · Patch".</summary>
    public string Line
    {
        get
        {
            var head = "ImpressiveBot · " + StateLabel;
            if (Link != ImpressiveBotLink.Live) return head;
            var names = Bots.Where(b => b.IsActive || b.NeedsInput || b.Failed)
                .Select(b => b.Bot).Distinct(StringComparer.Ordinal).ToList();
            return names.Count switch
            {
                0 => head,
                1 => head + " · " + names[0],
                2 => head + " · " + names[0] + " + " + names[1],
                _ => head + " · " + names[0] + " +" + (names.Count - 1),
            };
        }
    }

    /// <summary>Longer sentence for the settings tile: what is happening, and what to do.</summary>
    public string Detail
    {
        get
        {
            switch (Link)
            {
                case ImpressiveBotLink.NotInstalled:
                    return "MiMi.AgentCli.exe is not in %LOCALAPPDATA%\\MiMi, so ImpressiveBot has nowhere to report. Reinstall MiMi or point MIMI_AGENT_CLI at the CLI.";
                case ImpressiveBotLink.Ready:
                    return "The status mirror is installed. ImpressiveBot reports here while it runs; no status has arrived yet.";
                case ImpressiveBotLink.Stale:
                    return "No recent status from ImpressiveBot. Activity is unknown; check that ImpressiveBot is running with the updated status mirror.";
            }

            var parts = new List<string>();
            if (ActiveCount > 0)
                parts.Add(ActiveCount == 1 ? "1 bot working" : $"{ActiveCount} bots working");
            if (WaitingCount > 0)
                parts.Add(WaitingCount == 1 ? "1 waiting for you" : $"{WaitingCount} waiting for you");
            if (FailedCount > 0)
                parts.Add(FailedCount == 1 ? "1 failed" : $"{FailedCount} failed");
            if (parts.Count == 0) parts.Add("no bot is working");
            var newest = Bots.OrderByDescending(b => b.LastEventUtc).FirstOrDefault();
            if (newest is not null)
                parts.Add("last update " + Ago(newest.LastEventUtc));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Approvals are always answered in ImpressiveBot, never here.</summary>
    public const string ApprovalNote = "Approvals stay in ImpressiveBot.";

    public static string Ago(DateTimeOffset utc, DateTimeOffset? now = null)
    {
        var age = (now ?? DateTimeOffset.UtcNow) - utc;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age < TimeSpan.FromSeconds(60)) return $"{(int)age.TotalSeconds}s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        return $"{(int)age.TotalHours}h ago";
    }
}

/// <summary>
/// Builds <see cref="ImpressiveBotStatus"/> from MiMi's own session table. Pure: same sessions
/// in, same status out, so the settings tile and the island can never disagree.
/// </summary>
public static class ImpressiveBotStatusReader
{
    /// <summary>Where MiMi installs the CLI ImpressiveBot calls.</summary>
    public static string DefaultCliPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MiMi", "MiMi.AgentCli.exe");

    /// <summary>Resolves the CLI the same way ImpressiveBot does: MIMI_AGENT_CLI, then %LOCALAPPDATA%.</summary>
    public static string? ResolveCli(
        string? overridePath = null,
        string? localAppData = null,
        Func<string, bool>? exists = null)
    {
        var check = exists ?? File.Exists;
        var custom = (overridePath ?? Environment.GetEnvironmentVariable("MIMI_AGENT_CLI"))?.Trim();
        if (!string.IsNullOrEmpty(custom)) return check(custom) ? custom : null;
        var root = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) return null;
        var candidate = Path.Combine(root, "MiMi", "MiMi.AgentCli.exe");
        return check(candidate) ? candidate : null;
    }

    public static ImpressiveBotStatus Build(
        IReadOnlyList<AgentSession> sessions,
        string? cliPath,
        DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var bots = sessions
            .Where(s => ProviderNames.IsImpressiveBot(s.Provider))
            .Select(s => new ImpressiveBotBotStatus(
                Bot: ProviderNames.BotName(s.Provider) is { Length: > 0 } name ? name : "ImpressiveBot",
                State: s.State,
                Stale: s.Stale || s.Ended || at - s.LastEventUtc > AgentSessionManager.StaleAfter,
                Summary: s.LastSummary,
                Project: s.Project,
                LastEventUtc: s.LastEventUtc))
            .OrderBy(b => b.Bot, StringComparer.Ordinal)
            .ThenByDescending(b => b.LastEventUtc)
            .ToList();

        // A received fresh status proves the link works even when the sender uses a custom CLI path.
        var link = bots.Any(b => !b.Stale) ? ImpressiveBotLink.Live
            : bots.Count > 0 ? ImpressiveBotLink.Stale
            : cliPath is null ? ImpressiveBotLink.NotInstalled
            : ImpressiveBotLink.Ready;
        return new ImpressiveBotStatus(link, cliPath ?? "", bots);
    }
}
