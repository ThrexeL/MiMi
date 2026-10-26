using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MiMi.Agents;
using MiMi.Core;
using System.IO.Pipes;

namespace MiMi.Bridge;

/// <summary>
/// Frames used on the MiMi agent bridge (a single named pipe, length-prefixed JSON).
///
///   4-byte little-endian payload length, then UTF-8 JSON.
///
/// Client → MiMi:
///   { "kind": "hello",  "token": "...", "client": "codex" }        (first frame, mandatory)
///   { "kind": "event",  "event": { ...normalized AgentEvent... } }
/// MiMi → client:
///   { "kind": "ack",    "ok": true }  |  { "kind": "ack", "ok": false, "error": "..." }
///   { "kind": "approval_resolved", "approval_request_id": "...",
///     "decision": "approved|denied|expired", "by": "user", "at": "..." }
///
/// The pipe is created with an ACL granting only the current user and SYSTEM, so another
/// user's process cannot connect even before the token check.
/// </summary>
public sealed class BridgeProtocol
{
    public const int MaxFrameBytes = 64 * 1024;

    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("client")] public string? Client { get; set; }
    [JsonPropertyName("event")] public AgentEvent? Event { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("approval_request_id")] public string? ApprovalRequestId { get; set; }
    [JsonPropertyName("decision")] public string? Decision { get; set; }
    [JsonPropertyName("by")] public string? By { get; set; }
    [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOpts());

    public static BridgeProtocol? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<BridgeProtocol>(json, JsonOpts()); }
        catch (JsonException) { return null; }
    }

    private static JsonSerializerOptions JsonOpts() => new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = 10,
    };

    public static string PipeNameForUser() => "mimi-agent-bridge-" + Environment.UserName;

    /// <summary>Reads one length-prefixed frame. Returns null on clean EOF; throws on overrun.</summary>
    public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        for (var read = 0; read < 4;)
        {
            var n = await stream.ReadAsync(header.AsMemory(read, 4 - read), ct);
            if (n == 0) return read == 0 ? null : null; // clean EOF
            read += n;
        }
        var length = BitConverter.ToInt32(header, 0);
        if (length is < 0 or > MaxFrameBytes) throw new IOException($"frame too large: {length}");

        var payload = new byte[length];
        for (var read = 0; read < length;)
        {
            var n = await stream.ReadAsync(payload.AsMemory(read, length - read), ct);
            if (n == 0) throw new IOException("EOF inside frame body");
            read += n;
        }
        return Encoding.UTF8.GetString(payload);
    }

    public static async Task WriteFrameAsync(Stream stream, BridgeProtocol frame, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(frame.Serialize());
        if (payload.Length > MaxFrameBytes) throw new IOException("outgoing frame too large");
        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Creates a random bridge token and stores it user-private.</summary>
    public static string EnsureToken(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bridge-token");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length == 64) return existing;
        }
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(path, token);
        try
        {
            // Belt and suspenders: restrict to the current user.
            var security = new FileSecurity();
            var identity = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(identity);
            security.SetAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch { /* token dir already user-profile-private; best effort */ }
        return token;
    }

    /// <summary>PipeSecurity: current user + SYSTEM only.</summary>
    public static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        var identity = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(identity);
        security.AddAccessRule(new PipeAccessRule(identity, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
