using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bortle.Capture;

/// <summary>Where Bortle is and the key it gave this plugin.</summary>
public sealed record ClientSettings(string ServerUrl, string Key)
{
    /// <summary>
    /// An http(s) address and a whole plugin key: "bnp_" and 32 more
    /// characters (the server's secrets.token_urlsafe(24)). A key still being
    /// typed is not complete, so frames taken meanwhile wait for the profile's
    /// finished key instead of being filed under half of it.
    /// </summary>
    public bool IsComplete =>
        Uri.TryCreate(ServerUrl?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && KeyShape.IsMatch(Key?.Trim() ?? "");

    private static readonly System.Text.RegularExpressions.Regex KeyShape =
        new("^bnp_[A-Za-z0-9_-]{32,}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
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
    /// <summary>Nothing will go through until a person acts (quota full).</summary>
    Pause,
    /// <summary>No server address or no key in the active NINA profile.</summary>
    KeyMissing,
    /// <summary>The server refused the key (401/403): wrong key, revoked, or another profile's.</summary>
    KeyRejected,
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
    [JsonPropertyName("session")] public string? Session { get; init; }
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

    /// <summary>2: the server takes /capture/sequences/ (plugin 0.1.2). Older servers send none.</summary>
    [JsonPropertyName("protocol")] public int Protocol { get; init; }
}

public sealed record LivestreamShowing
{
    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("frames")] public int Frames { get; init; }
    [JsonPropertyName("integration_s")] public double IntegrationSeconds { get; init; }
}

/// <summary>The account's livestream as the server sees it. Never the stream key.</summary>
public sealed record LivestreamStatus
{
    [JsonPropertyName("allowed")] public bool Allowed { get; init; }
    [JsonPropertyName("configured")] public bool Configured { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = "off";
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("showing")] public LivestreamShowing? Showing { get; init; }

    public bool IsLive => State is "live" or "ending";
}

/// <summary>
/// The calls the plugin makes. Every endpoint lives under /capture/ and
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

    /// <summary>
    /// The same client pinned to these settings: the uploader sends every
    /// entry to the destination it was queued for, never to whatever the
    /// profile says by the time the request goes out.
    /// </summary>
    public BortleClient With(ClientSettings settings) => new(_http, () => settings);

    public Task<ApiResult<Hello>> HelloAsync(CancellationToken ct) =>
        SendAsync<Hello>(HttpMethod.Get, "capture/hello", null, ct);

    /// <summary>
    /// The livestream is made on the server from the frames this plugin
    /// uploads anyway; the plugin only switches it on and off.
    /// </summary>
    public Task<ApiResult<LivestreamStatus>> LivestreamAsync(CancellationToken ct) =>
        SendAsync<LivestreamStatus>(HttpMethod.Get, "capture/livestream", null, ct);

    public Task<ApiResult<LivestreamStatus>> StartLivestreamAsync(CancellationToken ct) =>
        SendAsync<LivestreamStatus>(HttpMethod.Post, "capture/livestream/start", null, ct);

    public Task<ApiResult<LivestreamStatus>> StopLivestreamAsync(CancellationToken ct) =>
        SendAsync<LivestreamStatus>(HttpMethod.Post, "capture/livestream/stop", null, ct);

    // Every request names its sequence; the server files it by that name
    // (services/orchestrator/capture.py, "Sequences by name"). The plugin keeps
    // no server session ids at all.

    public Task<ApiResult<SessionInfo>> StartSequenceAsync(string sequenceId, string? name, CancellationToken ct) =>
        SendAsync<SessionInfo>(
            HttpMethod.Post,
            $"capture/sequences/{Uri.EscapeDataString(sequenceId)}/start",
            new StringContent(JsonSerializer.Serialize(new { sequence = name }), Encoding.UTF8, "application/json"),
            ct);

    public Task<ApiResult<SessionInfo>> EndSequenceAsync(string sequenceId, CancellationToken ct) =>
        SendAsync<SessionInfo>(
            HttpMethod.Post,
            $"capture/sequences/{Uri.EscapeDataString(sequenceId)}/end",
            null,
            ct);

    /// <summary>
    /// Stream one frame from disk. The file is opened shared, so NINA (or a
    /// sync tool) may read it at the same time, and is never changed or deleted.
    /// </summary>
    public async Task<ApiResult<FrameResult>> PutFrameAsync(
        string sequenceId, string path, string? target, CancellationToken ct)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1 << 20, useAsync: true);
        }
        catch (FileNotFoundException)
        {
            return new(Outcome.Drop, null, $"{System.IO.Path.GetFileName(path)} is gone");
        }
        catch (DirectoryNotFoundException e)
        {
            return new(Outcome.Retry, null, e.Message); // a share that dropped out
        }
        catch (UnauthorizedAccessException e)
        {
            return new(Outcome.Retry, null, e.Message);
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
                $"capture/sequences/{Uri.EscapeDataString(sequenceId)}/frames/{Uri.EscapeDataString(name)}{query}",
                content,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        var settings = _settings();
        if (!settings.IsComplete)
        {
            return new(Outcome.KeyMissing, default, "Bortle server address or plugin key is missing");
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
            var outcome = Classify(response.StatusCode);
            if (outcome != Outcome.Ok)
            {
                var detail = response.StatusCode == HttpStatusCode.NotFound
                    ? "the server does not know this request (a Bortle older than 2026-11, or a proxy rule): "
                    : "";
                return new(outcome, default, $"HTTP {(int)response.StatusCode}: {detail}{Detail(body)}");
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

    /// <summary>
    /// What an answer means. A 404 is never "start something new": with
    /// sequences named by the plugin there is nothing to replace, so it is a
    /// route the server or a proxy does not have, retried with backoff.
    /// </summary>
    internal static Outcome Classify(HttpStatusCode status)
    {
        var code = (int)status;
        if (code is >= 200 and < 300) return Outcome.Ok;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Outcome.KeyRejected;
        if (status == HttpStatusCode.RequestEntityTooLarge) return Outcome.Pause; // quota
        if (status == HttpStatusCode.NotFound) return Outcome.Retry;
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
