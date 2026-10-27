using MiMi.AgentCli;
using MiMi.Agents;

// stdout is a DECISION CHANNEL for hooks — never log diagnostics to it. Everything
// informational goes to stderr, which agents record in their transcripts.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: MiMi.AgentCli <command> [options]");
    Console.Error.WriteLine("  claude-permission [--wait N] [--dry-run]   (stdin: PermissionRequest JSON; stdout: decision JSON)");
    Console.Error.WriteLine("  codex-permission  [--wait N] [--dry-run]   (stdin: Codex PermissionRequest JSON; stdout: decision JSON)");
    Console.Error.WriteLine("  activity --provider <claude-code|codex>    (stdin: lifecycle hook JSON; fire-and-forget)");
    Console.Error.WriteLine("  codex-notify '<json>'                      (Codex notify adapter; turn-complete toasts)");
    Console.Error.WriteLine("  install-claude [--timeout N] | uninstall-claude   (~/.claude/settings.json)");
    Console.Error.WriteLine("  install-codex  [--timeout N] | uninstall-codex    (~/.codex/hooks.json)");
    Console.Error.WriteLine("  install-codex-notify | uninstall-codex-notify     (~/.codex/config.toml, optional)");
    return 2;
}

var isPermissionCommand =
    string.Equals(args[0], "claude-permission", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(args[0], "codex-permission", StringComparison.OrdinalIgnoreCase);

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "claude-permission":
        case "codex-permission":
            return await PermissionHookAsync(args.Skip(1).ToArray(), args[0].ToLowerInvariant());
        case "activity":
            return await ActivityHookAsync(args.Skip(1).ToArray());
        case "codex-notify":
            return await CodexNotifyAsync(args.Length > 1 ? args[1] : "");
        case "install-claude":
        {
            var timeout = ParseOptionInt(args, "--timeout", 260);
            Console.WriteLine(Installers.InstallClaude(timeout));
            Console.Error.WriteLine("Note: Claude Code picks the hook up automatically; no trust step needed.");
            return 0;
        }
        case "uninstall-claude":
            Console.WriteLine(Installers.UninstallClaude());
            return 0;
        case "install-codex":
        {
            var timeout = ParseOptionInt(args, "--timeout", 260);
            Console.WriteLine(Installers.InstallCodex(timeout));
            Console.Error.WriteLine("Note: Codex requires a one-time trust review — run '/hooks' in the Codex TUI and approve the MiMi hook.");
            return 0;
        }
        case "uninstall-codex":
            Console.WriteLine(Installers.UninstallCodex());
            return 0;
        case "install-codex-notify":
            Console.WriteLine(CodexNotifyInstaller.Install());
            return 0;
        case "uninstall-codex-notify":
            Console.WriteLine(CodexNotifyInstaller.Uninstall());
            return 0;
        default:
            Console.Error.WriteLine($"unknown command '{args[0]}'");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"MiMi.AgentCli: {ex.GetType().Name}: {ex.Message}");
    // Hook commands must produce a decision even on unexpected failures: deny, fail closed.
    if (isPermissionCommand)
    {
        ClaudeHook.WriteDecision(Console.Out, ApprovalDecision.Denied,
            "MiMi approval flow failed internally — permission denied (fail-closed).");
        return 0;
    }
    return 1;
}

static int ParseOptionInt(string[] args, string name, int fallback)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out var v) && v > 0 ? v : fallback;
}

/// <summary>
/// Shared PermissionRequest hook flow for Claude Code and Codex: read stdin JSON,
/// show the approval in MiMi, wait (bounded) for the island decision, and emit the
/// provider-native decision JSON. Every failure path DENIES — never fail open.
/// </summary>
static async Task<int> PermissionHookAsync(string[] args, string command)
{
    var provider = command == "codex-permission" ? "codex" : "claude-code";
    var uninstallHint = provider == "codex" ? "uninstall-codex" : "uninstall-claude";
    var waitSeconds = ParseOptionInt(args, "--wait", 240);
    var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);

    using var stdin = Console.OpenStandardInput();
    using var reader = new StreamReader(stdin);
    var input = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(input))
    {
        // Fail closed but stay compatible: a deny decision the provider understands.
        ClaudeHook.WriteDecision(Console.Out, ApprovalDecision.Denied, "MiMi hook received no input — denied (fail-closed).");
        return 0;
    }

    ClaudeHook.ParsedRequest request;
    try
    {
        request = ClaudeHook.ParseStdin(input, provider);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"MiMi: could not parse hook input ({ex.Message})");
        ClaudeHook.WriteDecision(Console.Out, ApprovalDecision.Denied, "MiMi could not parse the permission request — denied (fail-closed).");
        return 0;
    }

    if (dryRun)
    {
        Console.Error.WriteLine($"DRY RUN — would send: {request.Event.ToJson()}");
        ClaudeHook.WriteDecision(Console.Out, ApprovalDecision.Denied, "dry run");
        return 0;
    }

    try
    {
        await using var bridge = await BridgeClient.ConnectAsync(TimeSpan.FromSeconds(5), $"{provider}-hook");
        await bridge.SendEventAsync(request.Event, TimeSpan.FromSeconds(5));
        Console.Error.WriteLine($"MiMi: approval '{request.Event.ApprovalRequestId}' sent ({request.DecisionSummary}); waiting up to {waitSeconds}s.");

        var decision = await bridge.WaitDecisionAsync(request.Event.ApprovalRequestId!, TimeSpan.FromSeconds(waitSeconds), default);
        var (mapped, reason) = decision switch
        {
            ApprovalDecision.Approved => (ApprovalDecision.Approved, "Approved via MiMi."),
            ApprovalDecision.Denied => (ApprovalDecision.Denied, "Denied via MiMi."),
            ApprovalDecision.Expired => (ApprovalDecision.Denied, "MiMi approval request expired unanswered — denied."),
            _ => (ApprovalDecision.Denied, $"MiMi approval timed out after {waitSeconds}s — denied (fail-closed)."),
        };
        ClaudeHook.WriteDecision(Console.Out, mapped, reason);
        return 0;
    }
    catch (IOException ex)
    {
        // MiMi not running / bridge disabled: NEVER fail open.
        Console.Error.WriteLine($"MiMi: {ex.Message}");
        ClaudeHook.WriteDecision(Console.Out, ApprovalDecision.Denied,
            $"MiMi is not reachable — permission denied (fail-closed). Start MiMi or remove its hook (MiMi.AgentCli {uninstallHint}).");
        return 0;
    }
}

/// <summary>
/// Fire-and-forget lifecycle hook: parse → send over the bridge → exit promptly.
/// Activity hooks must never block agent execution; a bridge outage is swallowed
/// (exit 0) so ordinary agent work is unaffected.
/// </summary>
static async Task<int> ActivityHookAsync(string[] args)
{
    var provider = ParseOptionString(args, "--provider", "claude-code");
    using var stdin = Console.OpenStandardInput();
    using var reader = new StreamReader(stdin);
    var input = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(input)) return 0;

    try
    {
        var ev = ActivityHook.Parse(input, provider);
        if (ev is null) return 0; // unsupported event for this provider: silent no-op
        await using var bridge = await BridgeClient.ConnectAsync(TimeSpan.FromSeconds(3), $"{provider}-activity");
        await bridge.SendEventAsync(ev, TimeSpan.FromSeconds(3));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"MiMi: activity skipped ({ex.Message})");
        return 0; // never disturb the agent over telemetry
    }
}

static string ParseOptionString(string[] args, string name, string fallback)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length && args[idx + 1].Length > 0 ? args[idx + 1] : fallback;
}

static async Task<int> CodexNotifyAsync(string json)
{
    if (string.IsNullOrWhiteSpace(json))
    {
        Console.Error.WriteLine("MiMi: codex-notify received no payload");
        return 0; // never disturb codex over a notify hiccup
    }
    try
    {
        var ev = CodexNotify.Parse(json);
        if (ev is null) return 0;
        await using var bridge = await BridgeClient.ConnectAsync(TimeSpan.FromSeconds(3), "codex-notify");
        await bridge.SendEventAsync(ev, TimeSpan.FromSeconds(3));
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"MiMi: notify skipped ({ex.Message})");
        return 0;
    }
}

/// <summary>Codex config.toml notify installer (optional turn-complete toasts): preserves existing notify settings.</summary>
static class CodexNotifyInstaller
{
    private static string NotifyLine()
    {
        var exe = (Environment.ProcessPath ?? "").Replace('\\', '/');
        return $"notify = [\"{exe}\", \"codex-notify\"]";
    }

    public static string Install()
    {
        var path = Installers.CodexConfigPath;
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var backup = path + $".mimi-backup-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}";
        if (File.Exists(path)) File.Copy(path, backup);
        var content = File.Exists(path) ? File.ReadAllText(path) : "";

        var lines = content.Split('\n').ToList();
        var existing = lines.FindIndex(l => l.TrimStart().StartsWith("notify", StringComparison.Ordinal) && l.Contains('='));
        if (existing >= 0)
        {
            if (lines[existing].Contains("MiMi.AgentCli", StringComparison.OrdinalIgnoreCase))
                return $"ALREADY INSTALLED: {path} already points notify at MiMi.";
            return $"SKIP: {path} already sets 'notify' for another program — leaving it untouched. Backup: {backup}";
        }
        lines.Add(NotifyLine());
        File.WriteAllText(path, string.Join('\n', lines));
        return $"INSTALLED: notify entry added to {path} (turn-complete toasts). Backup: {backup}";
    }

    public static string Uninstall()
    {
        var path = Installers.CodexConfigPath;
        if (!File.Exists(path)) return "NOTHING TO DO: no config.toml exists.";
        var backup = path + $".mimi-backup-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}";
        File.Copy(path, backup, overwrite: true);
        var lines = File.ReadAllText(path).Split('\n').ToList();
        var removed = lines.RemoveAll(l => l.TrimStart().StartsWith("notify", StringComparison.Ordinal)
                                           && l.Contains("MiMi.AgentCli", StringComparison.OrdinalIgnoreCase));
        if (removed == 0) return "NOTHING TO DO: no MiMi notify entry found.";
        File.WriteAllText(path, string.Join('\n', lines));
        return $"REMOVED: MiMi notify entry from {path}. Backup: {backup}";
    }
}
