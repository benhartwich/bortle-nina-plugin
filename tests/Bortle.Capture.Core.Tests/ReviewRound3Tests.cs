using System.Net;
using System.Reflection;
using Xunit;

namespace Bortle.Capture.Tests;

/// <summary>
/// The third, adversarial review before 0.1.2.0 (2026-11-17), on 6b7864a.
/// Every case here failed there and is held from now on.
/// </summary>
public sealed class ReviewRound3Tests : IDisposable
{
    private const string K1 = ReviewRound2Tests.K1;
    private const string K2 = ReviewRound2Tests.K2;
    private static readonly ClientSettings S1 = new("https://b.example", K1);
    private static readonly ClientSettings S2 = new("https://b.example", K2);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bortle-rev3-" + Guid.NewGuid().ToString("N"));
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

    private string Frame(string name)
    {
        var d = Path.Combine(_dir, "frames");
        Directory.CreateDirectory(d);
        var p = Path.Combine(d, name);
        File.WriteAllBytes(p, new byte[2880]);
        return p;
    }

    private Uploader Make(HttpMessageHandler h, Func<CaptureContext> ctx, SaveTracker? saves = null) =>
        new(
            new QueueStore(Path.Combine(_dir, "queues")),
            new BortleClient(new HttpClient(h), () => new ClientSettings("", "")),
            ctx,
            _log,
            () => _now,
            saves)
        {
            Poll = TimeSpan.FromMilliseconds(5),
        };

    private static List<string> Wire(FakeServer s) => s.Calls.Select(c => $"{c.Method} {c.Path}").ToList();

    private static async Task Steps(Uploader u, int n)
    {
        for (var i = 0; i < n; i++) await u.StepAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_night_sent_after_four_days_offline_is_still_one_session_with_its_end()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("p", S1));
        u.SequenceStarted("seq");
        for (var i = 0; i < 4; i++) u.FrameSaved(Frame($"{i}.fits"), null);
        u.SequenceFinished();
        _now += TimeSpan.FromDays(4); // a trip, or the server down
        await Steps(u, 20);
        var w = Wire(s);
        Assert.Single(w, c => c == "POST /capture/sessions");
        Assert.Equal("POST /capture/sessions/s1/end", w[^1]);
    }

    [Fact]
    public async Task One_passing_404_during_a_deploy_does_not_abandon_the_session()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("p", S1));
        u.SequenceStarted("seq");
        u.FrameSaved(Frame("1.fits"), null);
        await Steps(u, 3);
        u.FrameSaved(Frame("2.fits"), null);
        u.FrameSaved(Frame("3.fits"), null);
        u.SequenceFinished();
        s.Script.Enqueue(HttpStatusCode.NotFound); // the proxy, for a moment
        await Steps(u, 10);
        var w = Wire(s);
        Assert.Single(w, c => c == "POST /capture/sessions");
        Assert.Contains("PUT /capture/sessions/s1/frames/2.fits", w);
        Assert.Contains("PUT /capture/sessions/s1/frames/3.fits", w);
        Assert.Equal("POST /capture/sessions/s1/end", w[^1]);
    }

    [Fact]
    public async Task An_end_answered_404_once_is_tried_again()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("p", S1));
        u.SequenceStarted("seq");
        u.FrameSaved(Frame("1.fits"), null);
        await Steps(u, 3);
        u.SequenceFinished();
        s.Script.Enqueue(HttpStatusCode.NotFound);
        await Steps(u, 4);
        Assert.Equal(2, Wire(s).Count(c => c == "POST /capture/sessions/s1/end"));
    }

    [Fact]
    public async Task The_pending_queue_is_not_starved_by_lock_order()
    {
        var s = new FakeServer();
        var u1 = Make(s, () => new CaptureContext("P", S1));
        await u1.StepAsync(CancellationToken.None); // owns P+K1
        var noKey = Make(s, () => new CaptureContext("P", new ClientSettings("https://b.example", "")));
        noKey.FrameSaved(Frame("pending.fits"), null);
        var u2 = Make(s, () => new CaptureContext("P", S1));
        for (var i = 0; i < 10; i++)
        {
            await u2.StepAsync(CancellationToken.None);
            await u1.StepAsync(CancellationToken.None);
        }
        Assert.Contains(s.Calls, c => c.Path.EndsWith("pending.fits"));
        u1.Dispose();
        u2.Dispose();
        noKey.Dispose();
    }

    [Fact]
    public async Task A_save_that_never_becomes_a_frame_does_not_move_a_later_frame_to_the_previous_sequence()
    {
        var s = new FakeServer();
        var saves = new SaveTracker(() => _now);
        var u = Make(s, () => new CaptureContext("p", S1), saves);
        u.SequenceStarted("A");
        u.FrameSaved(Frame("A1.fits"), null);
        saves.SaveStarted(); // a snapshot: its ImageSaved never reaches FrameSaved
        u.SequenceStarted("B");
        saves.SaveFinished();
        _now += TimeSpan.FromHours(1);
        u.FrameSaved(Frame("B1.fits"), null);
        await Steps(u, 10);
        Assert.Contains("PUT /capture/sessions/s2/frames/B1.fits", Wire(s));
    }

    [Fact]
    public async Task A_carry_over_expires()
    {
        var s = new FakeServer();
        var saves = new SaveTracker(() => _now);
        var u = Make(s, () => new CaptureContext("p", S1), saves);
        u.SequenceStarted("A");
        saves.SaveStarted(); // a save that fails: no ImageSaved at all
        u.SequenceStarted("B");
        _now += SaveTracker.GiveUpAfter + TimeSpan.FromMinutes(1);
        u.FrameSaved(Frame("B1.fits"), null);
        await Steps(u, 10);
        Assert.Contains("PUT /capture/sessions/s2/frames/B1.fits", Wire(s));
    }

    [Fact]
    public async Task A_clock_stepped_back_keeps_the_order()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("p", S1));
        u.SequenceStarted("seq");
        _now -= TimeSpan.FromSeconds(30); // NTP correction
        u.FrameSaved(Frame("1.fits"), null);
        u.SequenceFinished();
        await Steps(u, 10);
        Assert.Equal(
            new[] { "POST /capture/sessions", "PUT /capture/sessions/s1/frames/1.fits", "POST /capture/sessions/s1/end" },
            Wire(s));
    }

    [Fact]
    public async Task A_deleted_queue_folder_neither_kills_the_loop_nor_loses_later_frames()
    {
        var s = new FakeServer();
        var u = Make(s, () => new CaptureContext("p", S1));
        u.FrameSaved(Frame("a.fits"), null);
        u.Start();
        try
        {
            await Task.Delay(300);
            Directory.Delete(Path.Combine(_dir, "queues"), true); // backlog cleared by hand, or a cleanup tool
            u.Wake();
            await Task.Delay(300);
            var loop = (Task?)typeof(Uploader).GetField("_loop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(u);
            Assert.False(loop!.IsCompleted, $"the loop ended: {loop.Exception}");
            u.FrameSaved(Frame("b.fits"), null); // must not throw
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && !s.Calls.Any(c => c.Path.EndsWith("/b.fits"))) await Task.Delay(20);
            Assert.Contains(s.Calls, c => c.Path.EndsWith("/b.fits"));
        }
        finally
        {
            await u.StopAsync();
        }
    }

    [Fact]
    public async Task Pending_entries_stay_with_the_first_key_that_took_them()
    {
        var s = new FakeServer();
        var key = "";
        var u = Make(s, () => new CaptureContext("P", new ClientSettings("https://b.example", key)));
        u.SequenceStarted("seq");
        u.FrameSaved(Frame("1.fits"), null);
        u.FrameSaved(Frame("2.fits"), null);
        u.SequenceFinished();
        key = K1;
        await u.StepAsync(CancellationToken.None); // the start went to K1
        key = K2;
        await Steps(u, 6);
        Assert.All(s.Calls, c => Assert.Equal("Bearer " + K1, c.Auth));
        key = K1;
        await Steps(u, 6);
        Assert.Equal("POST /capture/sessions/s1/end", Wire(s)[^1]);
    }

    [Fact]
    public async Task A_held_end_is_held_from_the_moment_it_is_written()
    {
        var s = new FakeServer();
        var saves = new SaveTracker(() => _now);
        var u = Make(s, () => new CaptureContext("p", S1), saves);
        u.SequenceStarted("seq");
        await u.StepAsync(CancellationToken.None);
        saves.SaveStarted();
        u.Start(); // the loop wakes on every write
        try
        {
            var end = u.EndAfterSavesAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
            await Task.Delay(200);
            Assert.DoesNotContain(s.Calls, c => c.Path.EndsWith("/end"));
            u.FrameSaved(Frame("last.fits"), null);
            saves.SaveFinished();
            _now += SaveTracker.DefaultQuiet + TimeSpan.FromSeconds(1);
            await end;
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && !s.Calls.Any(c => c.Path.EndsWith("/end"))) await Task.Delay(20);
            var w = Wire(s);
            Assert.True(w.IndexOf("PUT /capture/sessions/s1/frames/last.fits") < w.IndexOf("POST /capture/sessions/s1/end"));
        }
        finally
        {
            await u.StopAsync();
        }
    }
}
