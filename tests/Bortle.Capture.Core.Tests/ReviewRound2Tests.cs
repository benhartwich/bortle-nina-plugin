using System.Net;
using System.Text;
using Xunit;

namespace Bortle.Capture.Tests;

/// <summary>A server whose frame route always answers 404 (a proxy rule, a refused name).</summary>
internal sealed class Always404Frames : HttpMessageHandler
{
    public readonly List<string> Calls = new();
    private int _n;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var p = r.RequestUri!.PathAndQuery;
        Calls.Add($"{r.Method} {p}");
        if (p.Contains("/frames/"))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        }
        _n++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"id\":\"s{_n}\",\"state\":\"open\"}}", Encoding.UTF8, "application/json"),
        });
    }
}

/// <summary>
/// The second, adversarial review before 0.1.2.0 (2026-11-17): every case
/// here failed on b29ca7f and is held from now on.
/// </summary>
public sealed class ReviewRound2Tests : IDisposable
{
    internal const string K1 = "bnp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    internal const string K2 = "bnp_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly ClientSettings S1 = new("https://b.example", K1);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bortle-rev2-" + Guid.NewGuid().ToString("N"));
    private readonly NullLog _log = new();
    private DateTimeOffset _now = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string Frame(string name, string? folder = null)
    {
        var dir = Path.Combine(_dir, folder ?? "frames");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[2880]);
        return path;
    }

    private Uploader Make(HttpMessageHandler h, Func<CaptureContext> ctx, SaveTracker? saves = null, string? root = null) =>
        new(
            new QueueStore(root ?? Path.Combine(_dir, "queues")),
            new BortleClient(new HttpClient(h), () => new ClientSettings("", "")),
            ctx,
            _log,
            () => _now,
            saves)
        {
            Poll = TimeSpan.FromMilliseconds(5),
        };

    private static List<string> Wire(FakeServer s) => s.Calls.Select(c => $"{c.Method} {c.Path}").ToList();

    // 1 ------------------------------------------------------------------------------

    [Fact]
    public async Task A_route_that_always_404s_backs_off_instead_of_opening_sessions_forever()
    {
        var h = new Always404Frames();
        var u = Make(h, () => new CaptureContext("p", S1));
        u.FrameSaved(Frame("a.fits"), null);
        var waits = new List<TimeSpan?>();
        for (var i = 0; i < 5; i++) waits.Add(await u.StepAsync(CancellationToken.None));
        // one fresh session refused at once is a server/route problem, not a lost session
        Assert.All(waits.Skip(1), w => Assert.NotNull(w));
        Assert.True(h.Calls.Count(c => c == "POST /capture/sessions") <= 2);
        Assert.Equal(UploaderState.Retrying, u.Status.State);
    }

    // 2 ------------------------------------------------------------------------------

    [Fact]
    public async Task A_profile_switch_releases_the_queue_for_another_instance()
    {
        var s = new FakeServer();
        var profile = "P";
        var u1 = Make(s, () => new CaptureContext(profile, S1));
        u1.FrameSaved(Frame("first.fits"), null);
        await u1.StepAsync(CancellationToken.None); // owns P's queue
        profile = "Q";
        await u1.StepAsync(CancellationToken.None);
        var u2 = Make(s, () => new CaptureContext("P", S1));
        u2.FrameSaved(Frame("x.fits"), null);
        for (var i = 0; i < 5; i++) await u2.StepAsync(CancellationToken.None);
        Assert.NotEqual(UploadProblem.Busy, u2.Status.Problem);
        Assert.Equal(0, u2.Waiting);
        u1.Dispose();
        u2.Dispose();
    }

    [Fact]
    public async Task An_empty_pending_queue_held_elsewhere_does_not_block_this_key()
    {
        var s = new FakeServer();
        var u1 = Make(s, () => new CaptureContext("P", S1));
        await u1.StepAsync(CancellationToken.None);
        var u2 = Make(s, () => new CaptureContext("P", new ClientSettings("https://b.example", K2)));
        u2.FrameSaved(Frame("x.fits"), null);
        for (var i = 0; i < 5; i++) await u2.StepAsync(CancellationToken.None);
        Assert.NotEqual(UploadProblem.Busy, u2.Status.Problem);
        Assert.Contains(s.Calls, c => c.Path.EndsWith("/x.fits") && c.Auth == "Bearer " + K2);
        u1.Dispose();
        u2.Dispose();
    }

    // 3 ------------------------------------------------------------------------------

    [Fact]
    public async Task The_late_frame_window_counts_from_capture_not_from_sending()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("p", S1));
        u.SequenceStarted("seq");
        u.FrameSaved(Frame("1.fits"), null);
        u.SequenceFinished();
        _now += TimeSpan.FromMinutes(1);
        u.FrameSaved(Frame("late.fits"), null); // 1 min after the end
        for (var i = 0; i < 3; i++) await u.StepAsync(CancellationToken.None);
        _now += TimeSpan.FromMinutes(31); // the network was down meanwhile
        for (var i = 0; i < 3; i++) await u.StepAsync(CancellationToken.None);
        Assert.Contains("PUT /capture/sessions/s1/frames/late.fits", Wire(s));
        Assert.Single(Wire(s), c => c == "POST /capture/sessions");
    }

    [Fact]
    public async Task A_long_outage_mid_sequence_does_not_split_the_night()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("p", S1));
        u.SequenceStarted("seq");
        u.FrameSaved(Frame("1.fits"), null);
        _now += TimeSpan.FromMinutes(5);
        u.FrameSaved(Frame("2.fits"), null);
        await u.StepAsync(CancellationToken.None); // the start only
        _now += TimeSpan.FromHours(8);             // server unreachable all night
        for (var i = 0; i < 5; i++) await u.StepAsync(CancellationToken.None);
        Assert.Single(Wire(s), c => c == "POST /capture/sessions");
        Assert.Contains("PUT /capture/sessions/s1/frames/2.fits", Wire(s));
    }

    // 4 ------------------------------------------------------------------------------

    [Fact]
    public async Task The_last_image_of_A_still_being_saved_when_B_starts_stays_with_A()
    {
        var s = new FakeServer();
        var saves = new SaveTracker(() => _now);
        var u = Make(s, () => new CaptureContext("p", S1), saves);
        u.SequenceStarted("A");
        saves.SaveStarted();                         // A's last image is on its way
        var endA = u.EndAfterSavesAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
        u.SequenceStarted("B");                      // the next sequence starts at once
        u.FrameSaved(Frame("A_last.fits"), null);    // ...and only now A's image is written
        saves.SaveFinished();
        u.FrameSaved(Frame("B_1.fits"), null);
        _now += SaveTracker.DefaultQuiet + TimeSpan.FromSeconds(1);
        await endA;
        for (var i = 0; i < 10; i++) await u.StepAsync(CancellationToken.None);
        var wire = Wire(s);
        Assert.Contains("PUT /capture/sessions/s1/frames/A_last.fits", wire);
        Assert.Contains("PUT /capture/sessions/s2/frames/B_1.fits", wire);
        Assert.Contains("POST /capture/sessions/s1/end", wire);
        Assert.True(wire.IndexOf("PUT /capture/sessions/s1/frames/A_last.fits") < wire.IndexOf("POST /capture/sessions/s1/end"));
    }

    // 5 ------------------------------------------------------------------------------

    [Fact]
    public async Task A_key_missing_for_a_moment_keeps_the_order_and_one_session()
    {
        var s = new FakeServer();
        var key = K1;
        var u = Make(s, () => new CaptureContext("P", new ClientSettings("https://b.example", key)));
        u.SequenceStarted("seq");
        u.FrameSaved(Frame("1.fits"), null);
        key = "";
        u.FrameSaved(Frame("2.fits"), null);
        key = K1;
        u.FrameSaved(Frame("3.fits"), null);
        u.SequenceFinished();
        for (var i = 0; i < 10; i++) await u.StepAsync(CancellationToken.None);
        Assert.Equal(
            new[]
            {
                "POST /capture/sessions",
                "PUT /capture/sessions/s1/frames/1.fits",
                "PUT /capture/sessions/s1/frames/2.fits",
                "PUT /capture/sessions/s1/frames/3.fits",
                "POST /capture/sessions/s1/end",
            },
            Wire(s));
    }

    [Fact]
    public void A_half_typed_key_is_not_a_key()
    {
        Assert.True(new ClientSettings("https://bortle.eu", K1).IsComplete);
        Assert.False(new ClientSettings("https://bortle.eu", K1[..20]).IsComplete); // mid-typing
        Assert.False(new ClientSettings("https://bortle.eu", "bnp_").IsComplete);
        Assert.False(new ClientSettings("https://bortle.eu", "agent_" + K1[4..]).IsComplete); // not a plugin key
    }

    // 6 ------------------------------------------------------------------------------

    [Fact]
    public async Task The_end_survives_closing_NINA_right_after_the_sequence()
    {
        var s = new FakeServer();
        var saves = new SaveTracker(() => _now);
        var root = Path.Combine(_dir, "queues");
        var u = Make(s, () => new CaptureContext("P", S1), saves, root);
        u.SequenceStarted("seq");
        saves.SaveStarted(); // the last image is still being written
        _ = u.EndAfterSavesAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
        await u.StopAsync();
        u.Dispose(); // the user closes NINA

        var again = Make(s, () => new CaptureContext("P", S1), null, root);
        for (var i = 0; i < 5; i++) await again.StepAsync(CancellationToken.None);
        Assert.Contains("POST /capture/sessions/s1/end", Wire(s));
        again.Dispose();
    }

    [Fact]
    public async Task Uploads_switched_on_mid_sequence_still_end_that_sequence()
    {
        var s = new FakeServer();
        var enabled = false;
        var u = Make(s, () => new CaptureContext("P", S1, enabled));
        u.SequenceStarted("seq", queue: false); // switched off at the start: nothing queued
        enabled = true;
        u.FrameSaved(Frame("1.fits"), null);
        u.SequenceFinished();
        for (var i = 0; i < 6; i++) await u.StepAsync(CancellationToken.None);
        Assert.Equal(
            new[] { "POST /capture/sessions", "PUT /capture/sessions/s1/frames/1.fits", "POST /capture/sessions/s1/end" },
            Wire(s));
    }

    // 7 ------------------------------------------------------------------------------

    [Fact]
    public async Task A_frame_on_an_unreachable_folder_waits_instead_of_being_dropped()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("P", S1));
        var path = Frame("share.fits", "nas");
        u.FrameSaved(path, null);
        var nas = Path.Combine(_dir, "nas");
        Directory.Move(nas, nas + "-offline"); // the share drops out
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(1, u.Waiting);
        Assert.Equal(UploaderState.Retrying, u.Status.State);
        Directory.Move(nas + "-offline", nas); // and comes back
        for (var i = 0; i < 4; i++) await u.StepAsync(CancellationToken.None);
        Assert.Contains(s.Calls, c => c.Path.EndsWith("/share.fits"));
    }

    [Fact]
    public async Task A_frame_whose_folder_is_still_there_but_file_gone_is_dropped()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("P", S1));
        var path = Frame("gone.fits");
        u.FrameSaved(path, null);
        File.Delete(path);
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(0, u.Waiting);
    }

    // 8 ------------------------------------------------------------------------------

    [Fact]
    public async Task Finish_does_not_hold_the_sequence_while_nothing_can_be_sent()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("P", S1, UploadEnabled: false));
        u.Start();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ok = await u.FinishSessionAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.False(ok);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"held the sequence for {sw.Elapsed}");
        }
        finally
        {
            await u.StopAsync();
        }
    }

    // 9 ------------------------------------------------------------------------------

    [Fact]
    public void A_damaged_entry_is_set_aside_and_reported()
    {
        var dir = Path.Combine(_dir, "q");
        using var queue = new FrameQueue(dir);
        var reported = new List<string>();
        queue.Damaged += reported.Add;
        File.WriteAllText(Path.Combine(dir, "queue", "0000000000000000001-000000001-x.json"), "{ not json");
        Assert.Null(queue.Peek());
        Assert.Single(reported);
        Assert.Equal(1, queue.DamagedCount);
    }

    // 10 -----------------------------------------------------------------------------

    [Fact]
    public void Entries_of_the_old_version_are_shown_in_the_status()
    {
        var line = StatusText.Describe(
            new UploaderStatus(UploaderState.Idle, 0, null, null, null, Legacy: 3));
        Assert.Contains("3", line);
        Assert.Contains("älteren Plugin-Version", line);
    }
}
