using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using MiMi.Core;

namespace MiMi.App.AI;

public enum AiProviderKind
{
    EmbeddedCodex,
    ExternalCommand,
    Api,
}

public sealed record AiProviderDefinition(
    string Id,
    string Name,
    string Description,
    string Glyph,
    AiProviderKind Kind,
    string Command,
    string Arguments,
    bool IsBuiltIn,
    AiApiConfiguration? Api = null);

public sealed record AiProviderOption(
    AiProviderDefinition Provider,
    bool IsAvailable,
    string StatusLabel,
    string Detail);

public sealed record AiLaunchResult(bool Success, string Message);

public sealed record AiApiPreset(string Id, string Name, string Endpoint, string Model)
{
    public static AiApiPreset Grok { get; } = new("grok", "Grok API", "https://api.x.ai/v1", "grok-4.7");
    public static AiApiPreset OpenAI { get; } = new("openai", "OpenAI API", "https://api.openai.com/v1", "gpt-4.1-mini");
    public static AiApiPreset OpenRouter { get; } = new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "openai/gpt-4.1-mini");
    public static AiApiPreset Custom { get; } = new("custom", "Custom compatible API", "", "");

    public static AiApiPreset Find(string? id) => id switch
    {
        "openai" => OpenAI,
        "openrouter" => OpenRouter,
        "custom" => Custom,
        _ => Grok,
    };
}

/// <summary>Portable AI assistant discovery and launching. No user-specific paths are
/// embedded: CLIs are detected on PATH, and custom commands/API profiles are local settings.</summary>
public sealed class AiProviderService(AppSettings settings)
{
    private AppSettings _settings = settings;

    public void Update(AppSettings settings) => _settings = settings;

    public string DefaultProviderId => _settings.AiDefaultProvider;

    public IReadOnlyList<AiProviderOption> GetProviders()
    {
        var codexPath = DetectCommand("codex");
        var claudePath = DetectCommand("claude");
        var openCodePath = DetectCommand("opencode");
        var grokPath = DetectCommand("grok");
        var customCommand = _settings.AiCustomCommand;
        var customPath = string.IsNullOrWhiteSpace(customCommand) ? null : DetectCommand(customCommand);
        var customName = string.IsNullOrWhiteSpace(_settings.AiCustomName) ? "Custom AI" : _settings.AiCustomName;

        var providers = new List<AiProviderOption>
        {
            Option(new AiProviderDefinition("codex", "Codex",
                "Embedded, read-only conversation with one-shot approvals.", "\uE7C4",
                AiProviderKind.EmbeddedCodex, "codex", "", true), codexPath),
            Option(new AiProviderDefinition("claude", "Claude",
                "Opens Claude Code in its own foreground console window.", "\uE8B7",
                AiProviderKind.ExternalCommand, "claude", "", true), claudePath),
            Option(new AiProviderDefinition("opencode", "OpenCode",
                "Opens the OpenCode terminal assistant in the MiMi workspace.", "\uE713",
                AiProviderKind.ExternalCommand, "opencode", "", true), openCodePath),
            Option(new AiProviderDefinition("grok", "Grok",
                "Opens the Grok terminal assistant in the MiMi workspace.", "\uE77B",
                AiProviderKind.ExternalCommand, "grok", "", true), grokPath),
        };

        if (_settings.AiCustomMode == "api")
        {
            if (TryGetApiConfiguration(out var api, out var apiKey))
            {
                var available = !string.IsNullOrWhiteSpace(apiKey) || IsLoopback(api.Endpoint);
                providers.Add(new AiProviderOption(
                    new AiProviderDefinition("custom", customName,
                        "Text-only OpenAI-compatible API chat; no local tools or file access.", "\uE774",
                        AiProviderKind.Api, "", "", false, api),
                    available,
                    available ? "Ready" : "API key needed",
                    available
                        ? $"{api.Model} · {(apiKey is null ? "loopback endpoint without a key" : "API key configured (encrypted setting or MIMI_AI_API_KEY)")}."
                        : "Add an API key in Settings, or set MIMI_AI_API_KEY."));
            }
            else
            {
                providers.Add(new AiProviderOption(
                    new AiProviderDefinition("custom", customName,
                        "Text-only OpenAI-compatible API chat; no local tools or file access.", "\uE774",
                        AiProviderKind.Api, "", "", false),
                    false, "Configure API", "Settings → Integrations → AI assistants."));
            }
        }
        else
        {
            providers.Add(Option(new AiProviderDefinition("custom", customName,
                "Launch any AI CLI or application configured on this PC.", "\uE713",
                AiProviderKind.ExternalCommand, customCommand, _settings.AiCustomArguments, false), customPath));
        }
        return providers;
    }

    private static AiProviderOption Option(AiProviderDefinition provider, string? path) => new(
        provider,
        path is not null,
        path is not null ? "Ready" : provider.IsBuiltIn ? "Not found on PATH" : "Configure a command",
        path ?? (provider.IsBuiltIn
            ? $"Install {provider.Name} or add it to PATH."
            : "Settings → Integrations → AI assistants."));

    public AiProviderDefinition? GetDefaultProvider() =>
        GetProviders().FirstOrDefault(option => option.Provider.Id == DefaultProviderId)?.Provider;

    public AiLaunchResult Launch(AiProviderDefinition provider, string workspace)
    {
        if (provider.Kind is AiProviderKind.EmbeddedCodex or AiProviderKind.Api)
            return new AiLaunchResult(false, $"{provider.Name} opens inside MiMi.");

        var executable = DetectCommand(provider.Command);
        if (executable is null)
            return new AiLaunchResult(false, $"{provider.Name} was not found. Configure it in Settings → Integrations.");

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = provider.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workspace) ? Environment.CurrentDirectory : workspace,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal,
            };
            using var process = Process.Start(start);
            Logger.Info($"AI provider launched: {provider.Name}.");
            return new AiLaunchResult(process is not null,
                process is not null ? $"{provider.Name} opened." : $"{provider.Name} did not start.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Logger.Info($"AI provider launch failed: {provider.Name} ({ex.GetType().Name}).");
            return new AiLaunchResult(false, $"Could not open {provider.Name}: {ex.Message}");
        }
    }

    public bool TryGetApiConfiguration(out AiApiConfiguration configuration, out string? apiKey)
    {
        configuration = new AiApiConfiguration(_settings.AiApiEndpoint, _settings.AiApiModel,
            _settings.AiApiSystemPrompt, !string.IsNullOrWhiteSpace(_settings.AiApiKeyProtected));
        apiKey = AiApiKeyProtector.Unprotect(_settings.AiApiKeyProtected);
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("MIMI_AI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) apiKey = null;
        return AiApiClient.TryGetCompletionsUri(configuration.Endpoint, out _, out _)
               && !string.IsNullOrWhiteSpace(configuration.Model);
    }

    private static bool IsLoopback(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;

    public static string? DetectCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim().Trim('"');

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        var names = new[] { command, command + ".exe", command + ".cmd", command + ".bat" };
        var directories = new List<string> { Environment.CurrentDirectory };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        directories.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries));

        foreach (var directory in directories)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.GetFullPath(Path.Combine(directory.Trim().Trim('"'), name));
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // A malformed PATH entry must not block the remaining providers.
                }
            }
        }
        return null;
    }
}
