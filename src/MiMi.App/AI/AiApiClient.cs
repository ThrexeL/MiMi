using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MiMi.App.AI;

public sealed record AiApiConfiguration(string Endpoint, string Model, string SystemPrompt, bool HasStoredKey);
public sealed record AiChatMessage(string Role, string Content);
public sealed record AiChatResult(bool Success, string Content, string Error = "");

/// <summary>Text-only OpenAI-compatible chat client. It never exposes local files, tools,
/// command execution, or approval payloads to an API provider.</summary>
public sealed class AiApiClient(HttpClient? httpClient = null) : IDisposable
{
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120),
    };
    private readonly bool _ownsHttpClient = httpClient is null;

    public async Task<AiChatResult> SendAsync(AiApiConfiguration configuration, string? apiKey,
        IReadOnlyList<AiChatMessage> history, CancellationToken cancellationToken = default)
    {
        if (!TryGetCompletionsUri(configuration.Endpoint, out var endpoint, out var validationError))
            return new AiChatResult(false, "", validationError);
        if (string.IsNullOrWhiteSpace(configuration.Model))
            return new AiChatResult(false, "", "Enter an API model in Settings → Integrations → AI assistants.");
        if (!string.IsNullOrWhiteSpace(apiKey) && endpoint.Scheme == Uri.UriSchemeHttp
            && !endpoint.IsLoopback)
            return new AiChatResult(false, "", "Use an HTTPS API endpoint when sending an API key.");

        var messages = new List<ChatCompletionMessage>
        {
            new("system", configuration.SystemPrompt),
        };
        messages.AddRange(history.Select(message => new ChatCompletionMessage(message.Role, message.Content)));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new ChatCompletionRequest(configuration.Model, messages,
                Stream: false)),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var reason = (int)response.StatusCode switch
                {
                    401 or 403 => "The API rejected the key or access permissions.",
                    404 => "The API endpoint or model was not found.",
                    429 => "The API rate limit or quota was reached.",
                    >= 500 => "The API provider is temporarily unavailable.",
                    _ => $"The API returned HTTP {(int)response.StatusCode}.",
                };
                return new AiChatResult(false, "", reason);
            }

            var payload = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var content = payload?.Choices?.FirstOrDefault()?.Message is { } message
                ? ExtractContent(message.Content)
                : "";
            return string.IsNullOrWhiteSpace(content)
                ? new AiChatResult(false, "", "The API returned an empty response.")
                : new AiChatResult(true, content);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AiChatResult(false, "", "Request cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
            or NotSupportedException or InvalidOperationException)
        {
            // Do not log request headers, response bodies, prompts, or exception messages that may contain them.
            return new AiChatResult(false, "", $"Could not reach the API ({ex.GetType().Name}). Check the endpoint and connection.");
        }
    }

    public static bool TryGetCompletionsUri(string? endpoint, out Uri uri, out string error)
    {
        uri = null!;
        error = "";
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "Enter a valid HTTP(S) API base URL without a query string or fragment.";
            return false;
        }
        var path = parsed.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            path += "/chat/completions";
        uri = new Uri(parsed.GetLeftPart(UriPartial.Authority) + path, UriKind.Absolute);
        return true;
    }

    private static string ExtractContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        var text = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var value)
                && value.ValueKind == JsonValueKind.String)
                text.Add(value.GetString() ?? "");
        }
        return string.Concat(text);
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatCompletionMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record ChatCompletionMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")] public Choice[]? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public ResponseMessage? Message { get; set; }
    }

    private sealed class ResponseMessage
    {
        [JsonPropertyName("content")] public JsonElement Content { get; set; }
    }
}
