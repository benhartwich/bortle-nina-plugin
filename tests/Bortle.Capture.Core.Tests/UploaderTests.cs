using System.Net;
using System.Text;
using Xunit;

namespace Bortle.Capture.Tests;

/// <summary>A server that answers from a script and remembers what it was asked.</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public readonly List<(string Method, string Path, string? Auth, long Bytes)> Calls = new();
    public readonly Queue<HttpStatusCode> Script = new();
    private int _sessions;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        long bytes = 0;
        if (request.Content is not null)
        {
            bytes = (await request.Content.ReadAsByteArrayAsync(ct)).Length;
        }
        var path = request.RequestUri!.PathAndQuery;
        Calls.Add((request.Method.Method, path, request.Headers.Authorization?.ToString(), bytes));
        var status = Script.Count > 0 ? Script.Dequeue() : HttpStatusCode.OK;
        if (status != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status) { Content = Json("{\"detail\":\"no\"}") };
        }
        if (path.EndsWith("/capture/sessions"))
        {
            _sessions++;
            return new HttpResponseMessage(status)
            {
                Content = Json($"{{\"id\":\"s{_sessions}\",\"state\":\"open\",\"level\":\"guarded\"}}"),
            };
        }
        if (path.Contains("/frames/"))
        {
            return new HttpResponseMessage(status) { Content = Json("{\"stored\":true,\"project\":\"p\"}") };
        }
        return new HttpResponseMessage(status) { Content = Json("{\"id\":\"x\",\"state\":\"closed\"}") };
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");
}

internal sealed class NullLog : ILog
{
    public readonly List<string> Errors = new();
    public void Info(string message) { }
    public void Error(string message) => Errors.Add(message);
}

public sealed class UploaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bortle-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServer _server = new();
    private readonly NullLog _log = new();
    private DateTimeOffset _now = new(2026, 10, 12, 20, 0, 0, TimeSpan.Zero);
    private ClientSettings _settings = new("https://bortle.example/", "bnp_secret");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private (FrameQueue, Uploader) Make()
    {
        var queue = new FrameQueue(_dir);
        var client = new BortleClient(new HttpClient(_server), () => _settings);
        return (queue, new Uploader(queue, client, _log, () => _now));
    }

    private string Frame(string name, int size = 2880)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "frames"));
        var path = Path.Combine(_dir, "frames", name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static async Task Drain(Uploader uploader, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            var wait = await uploader.StepAsync(CancellationToken.None);
            if (uploader.Status.State == UploaderState.Idle) return;
            if (wait is not null && uploader.Status.State != UploaderState.Sending) return;
        }
    }

    [Fact]
    public async Task A_night_goes_out_in_the_order_it_happened()
    {
        var (queue, uploader) = Make();
        uploader.SequenceStarted("M42");
        uploader.FrameSaved(Frame("M42_0001.fits", 5760), "M 42");
        uploader.FrameSaved(Frame("M42_0002.fits"), "M 42");
        uploader.SequenceFinished();
        await Drain(uploader);

        Assert.Equal(
            new[]
            {
                "POST /capture/sessions",
                "PUT /capture/sessions/s1/frames/M42_0001.fits?target=M%2042",
                "PUT /capture/sessions/s1/frames/M42_0002.fits?target=M%2042",
                "POST /capture/sessions/s1/end",
            },
            _server.Calls.Select(c => $"{c.Method} {c.Path}"));
        Assert.All(_server.Calls, c => Assert.Equal("Bearer bnp_secret", c.Auth));
        Assert.Equal(5760, _server.Calls[1].Bytes); // the whole file, streamed
        Assert.Equal(0, queue.Count);
        Assert.Null(queue.LoadSession());
    }

    [Fact]
    public async Task A_server_failure_keeps_the_frame_and_backs_off()
    {
        var (queue, uploader) = Make();
        uploader.FrameSaved(Frame("a.fits"), null);
        _server.Script.Enqueue(HttpStatusCode.OK); // the implicit session start
        _server.Script.Enqueue(HttpStatusCode.BadGateway);
        _server.Script.Enqueue(HttpStatusCode.ServiceUnavailable);

        Assert.Equal(Uploader.FirstRetry, await uploader.StepAsync(CancellationToken.None));
        Assert.Equal(UploaderState.Retrying, uploader.Status.State);
        Assert.Equal(Uploader.FirstRetry * 2, await uploader.StepAsync(CancellationToken.None));
        Assert.Equal(1, queue.Count);
        Assert.Null(await uploader.StepAsync(CancellationToken.None)); // through
        Assert.Equal(0, queue.Count);
        // one session for all three attempts
        Assert.Single(_server.Calls, c => c.Path == "/capture/sessions");
    }

    [Fact]
    public async Task A_wrong_key_pauses_without_losing_anything()
    {
        var (queue, uploader) = Make();
        uploader.FrameSaved(Frame("a.fits"), null);
        _server.Script.Enqueue(HttpStatusCode.Unauthorized);
        Assert.Equal(Uploader.PauseFor, await uploader.StepAsync(CancellationToken.None));
        Assert.Equal(UploaderState.Paused, uploader.Status.State);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task Without_settings_nothing_is_sent()
    {
        _settings = new ClientSettings("", "");
        var (queue, uploader) = Make();
        uploader.FrameSaved(Frame("a.fits"), null);
        Assert.Equal(Uploader.PauseFor, await uploader.StepAsync(CancellationToken.None));
        Assert.Empty(_server.Calls);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task A_frame_the_server_refuses_is_skipped_and_logged()
    {
        var (queue, uploader) = Make();
        uploader.FrameSaved(Frame("bad.fits"), null);
        uploader.FrameSaved(Frame("good.fits"), null);
        _server.Script.Enqueue(HttpStatusCode.OK);
        _server.Script.Enqueue(HttpStatusCode.BadRequest);
        await Drain(uploader);
        Assert.Equal(0, queue.Count);
        Assert.Contains(_server.Calls, c => c.Path.Contains("good.fits"));
        Assert.Contains(_log.Errors, e => e.Contains("bad.fits"));
    }

    [Fact]
    public async Task A_deleted_frame_does_not_block_the_queue()
    {
        var (queue, uploader) = Make();
        var gone = Frame("gone.fits");
        uploader.FrameSaved(gone, null);
        uploader.FrameSaved(Frame("next.fits"), null);
        File.Delete(gone);
        await Drain(uploader);
        Assert.Equal(0, queue.Count);
        Assert.DoesNotContain(_server.Calls, c => c.Path.Contains("gone.fits"));
    }

    [Fact]
    public async Task The_queue_survives_a_restart()
    {
        var (_, first) = Make();
        first.SequenceStarted(null);
        first.FrameSaved(Frame("a.fits"), null);
        await first.StepAsync(CancellationToken.None); // only the start went out
        first.Dispose();

        var (queue, again) = Make();
        Assert.Equal(1, queue.Count);
        Assert.Equal("s1", queue.LoadSession()?.Id);
        await Drain(again);
        Assert.Equal("PUT /capture/sessions/s1/frames/a.fits", $"{_server.Calls[^1].Method} {_server.Calls[^1].Path}");
    }

    [Fact]
    public async Task Last_nights_unended_session_is_not_tonights()
    {
        var (queue, uploader) = Make();
        uploader.SequenceStarted(null);
        uploader.FrameSaved(Frame("a.fits"), null);
        await Drain(uploader);
        _now += Uploader.SessionIdle + TimeSpan.FromHours(1);
        uploader.FrameSaved(Frame("b.fits"), null);
        await Drain(uploader);
        Assert.Contains(_server.Calls, c => c.Path == "/capture/sessions/s2/frames/b.fits");
    }

    [Fact]
    public async Task A_session_the_server_forgot_is_replaced()
    {
        var (queue, uploader) = Make();
        uploader.SequenceStarted(null);
        await Drain(uploader);
        uploader.FrameSaved(Frame("a.fits"), null);
        _server.Script.Enqueue(HttpStatusCode.NotFound);
        await Drain(uploader);
        Assert.Equal("/capture/sessions/s2/frames/a.fits", _server.Calls[^1].Path);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Ending_twice_reports_once()
    {
        var (_, uploader) = Make();
        uploader.SequenceStarted(null);
        uploader.SequenceFinished(); // the instruction
        uploader.SequenceFinished(); // then NINA's own event
        await Drain(uploader);
        Assert.Single(_server.Calls, c => c.Path.EndsWith("/end"));
    }

    [Fact]
    public async Task Finishing_waits_for_the_frames_before_it()
    {
        var (queue, uploader) = Make();
        uploader.SequenceStarted(null);
        uploader.FrameSaved(Frame("a.fits"), null);
        uploader.Start();
        try
        {
            Assert.True(await uploader.FinishSessionAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            Assert.Equal(0, queue.Count);
            Assert.EndsWith("/end", _server.Calls[^1].Path);
        }
        finally
        {
            await uploader.StopAsync();
        }
    }

    [Fact]
    public async Task Finishing_gives_up_waiting_but_keeps_the_end_queued()
    {
        _settings = new ClientSettings("", ""); // nothing can go out
        var (queue, uploader) = Make();
        uploader.FrameSaved(Frame("a.fits"), null);
        _now = DateTimeOffset.UtcNow;
        var clockStart = _now;
        var task = uploader.FinishSessionAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        _now = clockStart + TimeSpan.FromSeconds(3);
        Assert.False(await task);
        Assert.Equal(2, queue.Count); // the frame and the end, both still there
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false, Outcome.Ok)]
    [InlineData(HttpStatusCode.Forbidden, false, Outcome.Pause)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, true, Outcome.Pause)]
    [InlineData(HttpStatusCode.NotFound, true, Outcome.SessionGone)]
    [InlineData(HttpStatusCode.NotFound, false, Outcome.Retry)]
    [InlineData(HttpStatusCode.BadRequest, true, Outcome.Drop)]
    [InlineData(HttpStatusCode.TooManyRequests, true, Outcome.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, true, Outcome.Retry)]
    public void Answers_are_classified(HttpStatusCode status, bool sessionScoped, Outcome expected) =>
        Assert.Equal(expected, BortleClient.Classify(status, sessionScoped));

    [Theory]
    [InlineData("https://bortle.eu", "bnp_x", true)]
    [InlineData("http://192.168.1.20:3100", "bnp_x", true)]
    [InlineData("bortle.eu", "bnp_x", false)]
    [InlineData("https://bortle.eu", " ", false)]
    [InlineData("ftp://bortle.eu", "bnp_x", false)]
    public void Settings_are_complete_only_with_an_http_address_and_a_key(string url, string key, bool ok) =>
        Assert.Equal(ok, new ClientSettings(url, key).IsComplete);
}
