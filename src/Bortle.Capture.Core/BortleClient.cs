using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bortle.Capture;

/// <summary>Where Bortle is and the key it gave this plugin.</summary>
public sealed record ClientSettings(string ServerUrl, string Key)
{
    public bool IsComplete =>
        Uri.TryCreate(ServerUrl?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrWhiteSpace(Key);
}

/// <summary>What to do with an answer.</summary>
public enum Outcome
{
    /// <summary>Done; on to the next entry.</summary>
    Ok,
    /// <summary>The network or the server failed; try the same entry again later.</summary>
    Retry,
    /// <summary>The server will never take this (not a FITS frame); skip it.</summary>
    Drop,
    /// <summary>Nothing will go through until a person acts (wrong key, quota full).</summary>
    Pause,
    /// <summary>The server does not know our session (any more); start a new one.</summary>
    SessionGone,
}

public sealed record ApiResult<T>(Outcome Outcome, T? Value, string? Message);

public sealed record SessionInfo
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("level")] public string? Level { get; init; }
}

public sealed record FrameResult
{
    [JsonPropertyName("stored")] public bool Stored { get; init; }
    [JsonPropertyName("duplicate")] public bool Duplicate { get; init; }
    [JsonPropertyName("project")] public string? Project { get; init; }
    [JsonPropertyName("bucket")] public string? Bucket { get; init; }
}

public sealed record HelloPlan
{
    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("develop_level")] public string? DevelopLevel { get; init; }
}

public sealed record HelloLevel
{
    [JsonPropertyName("level")] public string? Level { get; init; }
}

public sealed record Hello
{
    [JsonPropertyName("owner")] public string Owner { get; init; } = "";
    [JsonPropertyName("plan")] public HelloPlan? Plan { get; init; }
    [JsonPropertyName("level")] public HelloLevel? Level { get; init; }
}

/// <summary>
/// The four calls the plugin makes. Every endpoint lives under /capture/ and
/// takes the plugin key as a bearer token. Nothing here throws for a network
/// or server problem: each call answers with an <see cref="Outcome"/>.
/// </summary>
public sealed class BortleClient
{
    private readonly HttpClient _http;
    private readonly Func<ClientSettings> _settings;

    public BortleClient(HttpClient http, Func<ClientSettings> settings)
    {
        _http = http;
        _settings = settings;
    }

    public Task<ApiResult<Hello>> HelloAsync(CancellationToken ct) =>
        SendAsync<Hello>(HttpMethod.Get, "capture/hello", null, ct);

    public Task<ApiResult<SessionInfo>> StartSessionAsync(string? sequence, CancellationToken ct) =>
        SendAsync<SessionInfo>(
            HttpMethod.Post,
            "capture/sessions",
            new StringContent(JsonSerializer.Serialize(new { sequence }), Encoding.UTF8, "application/json"),
            ct);

    public Task<ApiResult<SessionInfo>> EndSessionAsync(string sessionId, CancellationToken ct) =>
        SendAsync<SessionInfo>(
            HttpMethod.Post,
            $"capture/sessions/{Uri.EscapeDataString(sessionId)}/end",
            null,
            ct,
            sessionScoped: true);

    /// <summary>
    /// Stream one frame from disk. The file is opened shared, so NINA (or a
    /// sync tool) may read it at the same time, and is never changed or deleted.
    /// </summary>
    public async Task<ApiResult<FrameResult>> PutFrameAsync(
        string sessionId, string path, string? target, CancellationToken ct)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1 << 20, useAsync: true);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(Outcome.Drop, null, $"{System.IO.Path.GetFileName(path)} is gone");
        }
        catch (IOException e)
        {
            return new(Outcome.Retry, null, e.Message); // still being written, or locked
        }
        using (stream)
        {
            var name = System.IO.Path.GetFileName(path);
            var query = string.IsNullOrWhiteSpace(target) ? "" : $"?target={Uri.EscapeDataString(target)}";
            var content = new StreamContent(stream, 1 << 20);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/fits");
            content.Headers.ContentLength = stream.Length;
            return await SendAsync<FrameResult>(
                HttpMethod.Put,
                $"capture/sessions/{Uri.EscapeDataString(sessionId)}/frames/{Uri.EscapeDataString(name)}{query}",
                content,
                ct,
                sessionScoped: true).ConfigureAwait(false);
        }
    }

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct, bool sessionScoped = false)
    {
        var settings = _settings();
        if (!settings.IsComplete)
        {
            return new(Outcome.Pause, default, "Bortle server address or plugin key is missing");
        }
        var baseUri = new Uri(settings.ServerUrl.Trim().TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Key.Trim());
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var outcome = Classify(response.StatusCode, sessionScoped);
            if (outcome != Outcome.Ok)
            {
                return new(outcome, default, $"HTTP {(int)response.StatusCode}: {Detail(body)}");
            }
            try
            {
                return new(Outcome.Ok, JsonSerializer.Deserialize<T>(body), null);
            }
            catch (JsonException)
            {
                // A proxy's HTML page answering 200 is not Bortle.
                return new(Outcome.Retry, default, "the server's answer was not Bortle's");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return new(Outcome.Retry, default, e.Message);
        }
    }

    internal static Outcome Classify(HttpStatusCode status, bool sessionScoped)
    {
        var code = (int)status;
        if (code is >= 200 and < 300) return Outcome.Ok;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Outcome.Pause;
        if (status == HttpStatusCode.RequestEntityTooLarge) return Outcome.Pause; // quota
        if (status == HttpStatusCode.NotFound) return sessionScoped ? Outcome.SessionGone : Outcome.Retry;
        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests) return Outcome.Retry;
        if (code is >= 400 and < 500) return Outcome.Drop;
        return Outcome.Retry;
    }

    private static string Detail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
            {
                return detail.ValueKind == JsonValueKind.String ? detail.GetString() ?? "" : detail.ToString();
            }
        }
        catch (JsonException)
        {
        }
        return body.Length > 200 ? body[..200] : body;
    }
}
