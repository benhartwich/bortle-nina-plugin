using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bortle.Capture.Tests;

/// <summary>
/// A Bortle server that answers from a script and remembers what it was asked.
/// Sequence ids (32 hex digits) are shown in <see cref="Wire"/> as q1, q2 …
/// in the order they first appear, so tests read like the night they describe.
/// </summary>
internal sealed class FakeServer : HttpMessageHandler
{
    private readonly List<(string Method, string Path, string? Auth, long Bytes)> _calls = new();

    /// <summary>A copy: the upload loop adds from its own thread.</summary>
    public List<(string Method, string Path, string? Auth, long Bytes)> Calls
    {
        get
        {
            lock (_calls) return _calls.ToList();
        }
    }
    public readonly Queue<HttpStatusCode> Script = new();
    private readonly Dictionary<string, string> _alias = new();
    private static readonly Regex SequenceId = new("[a-f0-9]{32}");

    public List<string> Wire => Calls.Select(c => $"{c.Method} {Alias(c.Path)}").ToList();

    public string Alias(string path)
    {
        lock (_alias) return AliasLocked(path);
    }

    private string AliasLocked(string path) =>
        SequenceId.Replace(path, m =>
        {
            if (!_alias.TryGetValue(m.Value, out var name))
            {
                name = $"q{_alias.Count + 1}";
                _alias[m.Value] = name;
            }
            return name;
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        long bytes = 0;
        if (request.Content is not null)
        {
            bytes = (await request.Content.ReadAsByteArrayAsync(ct)).Length;
        }
        var path = request.RequestUri!.PathAndQuery;
        lock (_calls)
        {
            _calls.Add((request.Method.Method, path, request.Headers.Authorization?.ToString(), bytes));
            Alias(path);
        }
        var status = Script.Count > 0 ? Script.Dequeue() : HttpStatusCode.OK;
        if (status != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status) { Content = Json("{\"detail\":\"no\"}") };
        }
        if (path.Contains("/frames/"))
        {
            return new HttpResponseMessage(status) { Content = Json("{\"stored\":true,\"project\":\"p\",\"session\":\"n1\"}") };
        }
        if (path.Contains("/livestream"))
        {
            return new HttpResponseMessage(status) { Content = Json("{\"allowed\":true,\"configured\":true,\"state\":\"off\"}") };
        }
        return new HttpResponseMessage(status) { Content = Json("{\"id\":\"n1\",\"state\":\"open\",\"level\":\"guarded\"}") };
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
}

internal sealed class NullLog : ILog
{
    public readonly List<string> Errors = new();
    public void Info(string message) { }
    public void Error(string message) => Errors.Add(message);
}

internal static class Keys
{
    public const string K1 = "bnp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public const string K2 = "bnp_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
}
