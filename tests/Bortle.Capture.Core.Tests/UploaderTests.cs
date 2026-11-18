using System.Net;
using System.Reflection;
using Xunit;
using static Bortle.Capture.Tests.Keys;

namespace Bortle.Capture.Tests;

/// <summary>
/// The upload queue, night by night. Includes every case from the N.I.N.A.
/// manifest review (#795) and the three adversarial review rounds before
/// 0.1.2.0 that still apply to the sequence-id design.
/// </summary>
public sealed class UploaderTests : IDisposable
{
    private static readonly ClientSettings S1 = new("https://a.example/", K1);
    private static readonly ClientSettings S2 = new("https://b.example/", K2);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bortle-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServer _server = new();
    private readonly NullLog _log = new();
    private DateTimeOffset _now = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);
    private CaptureContext _context = new("profile-1", S1);

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

    private Uploader Make(SaveTracker? saves = null) =>
        new(
            new QueueStore(Path.Combine(_dir, "queues"), Path.Combine(_dir, "queue")),
            // the client's own settings are never what is sent with
            new BortleClient(new HttpClient(_server), () => new ClientSettings("https://wrong.example/", K2)),
            () => _context,
            _log,
            () => _now,
            saves)
        {
            Poll = TimeSpan.FromMilliseconds(5),
            SettingsSettle = TimeSpan.FromMilliseconds(200),
        };

    private string Frame(string name, string folder = "frames")
    {
        var dir = Path.Combine(_dir, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[2880]);
        return path;
    }

    private static async Task Drain(Uploader u, int steps = 30)
    {
        for (var i = 0; i < steps; i++)
        {
            var wait = await u.StepAsync(CancellationToken.None);
            if (wait is not null && u.Status.State != UploaderState.Sending) return;
        }
    }

    private static async Task<bool> Eventually(Func<bool> done, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (done()) return true;
            await Task.Delay(20);
        }
        return done();
    }

    // --- a night ------------------------------------------------------------------

    [Fact]
    public async Task A_night_goes_out_in_the_order_it_happened_named_by_its_sequence()
    {
        var u = Make();
        u.SequenceStarted("M42");
        u.FrameSaved(Frame("M42_0001.fits"), "M 42");
        u.FrameSaved(Frame("M42_0002.fits"), "M 42");
        u.SequenceFinished();
        await Drain(u);
        Assert.Equal(
            new[]
            {
                "POST /capture/sequences/q1/start",
                "PUT /capture/sequences/q1/frames/M42_0001.fits?target=M%2042",
                "PUT /capture/sequences/q1/frames/M42_0002.fits?target=M%2042",
                "POST /capture/sequences/q1/end",
            },
            _server.Wire);
        Assert.All(_server.Calls, c => Assert.Equal("Bearer " + K1, c.Auth));
        Assert.Equal(2880, _server.Calls[1].Bytes);
        Assert.Equal(0, u.Waiting);
    }

    [Fact]
    public async Task A_server_failure_keeps_the_frame_and_backs_off()
    {
        var u = Make();
        u.FrameSaved(Frame("a.fits"), null);
        _server.Script.Enqueue(HttpStatusCode.BadGateway);
        _server.Script.Enqueue(HttpStatusCode.ServiceUnavailable);
        Assert.Equal(Uploader.FirstRetry, await u.StepAsync(CancellationToken.None));
        Assert.Equal(Uploader.FirstRetry * 2, await u.StepAsync(CancellationToken.None));
        Assert.Equal(1, u.Waiting);
        Assert.Null(await u.StepAsync(CancellationToken.None));
        Assert.Equal(0, u.Waiting);
    }

    [Fact]
    public async Task A_404_is_retried_with_backoff_and_opens_nothing()
    {
        // a route the server or a proxy does not have: never a reason to start anything
        var u = Make();
        u.FrameSaved(Frame("a.fits"), null);
        for (var i = 0; i < 5; i++) _server.Script.Enqueue(HttpStatusCode.NotFound);
        var waits = new List<TimeSpan?>();
        for (var i = 0; i < 5; i++) waits.Add(await u.StepAsync(CancellationToken.None));
        Assert.All(waits, w => Assert.NotNull(w));
        Assert.True(waits[^1] > waits[0]);
        Assert.All(_server.Wire, c => Assert.Equal("PUT /capture/sequences/q1/frames/a.fits", c));
        Assert.Contains("older than 2026-11", u.Status.Message);
        Assert.Null(await u.StepAsync(CancellationToken.None));
        Assert.Equal(0, u.Waiting);
    }

    [Fact]
    public async Task A_wrong_key_pauses_briefly_without_losing_anything()
    {
        var u = Make();
        u.FrameSaved(Frame("a.fits"), null);
        _server.Script.Enqueue(HttpStatusCode.Unauthorized);
        Assert.Equal(Uploader.KeyPause, await u.StepAsync(CancellationToken.None));
        Assert.Equal(UploadProblem.KeyRejected, u.Status.Problem);
        Assert.Equal(1, u.Waiting);
    }

    [Fact]
    public async Task A_full_quota_keeps_the_long_pause()
    {
        var u = Make();
        u.FrameSaved(Frame("a.fits"), null);
        _server.Script.Enqueue(HttpStatusCode.RequestEntityTooLarge);
        Assert.Equal(Uploader.PauseFor, await u.StepAsync(CancellationToken.None));
        Assert.Equal(UploadProblem.Quota, u.Status.Problem);
    }

    [Fact]
    public async Task A_frame_the_server_refuses_is_skipped_and_logged()
    {
        var u = Make();
        u.FrameSaved(Frame("bad.fits"), null);
        u.FrameSaved(Frame("good.fits"), null);
        _server.Script.Enqueue(HttpStatusCode.BadRequest);
        await Drain(u);
        Assert.Contains(_server.Calls, c => c.Path.Contains("good.fits"));
        Assert.Contains(_log.Errors, e => e.Contains("bad.fits"));
    }

    [Fact]
    public async Task The_queue_survives_a_restart()
    {
        var first = Make();
        first.SequenceStarted("M42");
        first.FrameSaved(Frame("a.fits"), null);
        first.Dispose();
        var again = Make(); // NINA never said the sequence finished: the new run ends it
        Assert.Equal(3, again.Waiting);
        await Drain(again);
        Assert.Equal(
            new[] { "POST /capture/sequences/q1/start", "PUT /capture/sequences/q1/frames/a.fits", "POST /capture/sequences/q1/end" },
            _server.Wire);
    }

    [Fact]
    public async Task A_night_sent_days_later_is_still_one_sequence_with_its_end()
    {
        var u = Make();
        u.SequenceStarted("seq");
        for (var i = 0; i < 4; i++) u.FrameSaved(Frame($"{i}.fits"), null);
        u.SequenceFinished();
        _now += TimeSpan.FromDays(4);
        await Drain(u);
        Assert.All(_server.Wire, c => Assert.Contains("/q1/", c));
        Assert.Equal("POST /capture/sequences/q1/end", _server.Wire[^1]);
    }

    // --- 1. ownership ---------------------------------------------------------------

    [Fact]
    public async Task A_frame_queued_for_account_A_is_never_sent_to_account_B()
    {
        var u = Make();
        u.FrameSaved(Frame("a.fits"), null);
        _context = _context with { Settings = S2 };
        u.FrameSaved(Frame("b.fits"), null);
        await Drain(u);
        Assert.All(_server.Calls, c => Assert.Equal("Bearer " + K2, c.Auth));
        Assert.DoesNotContain(_server.Calls, c => c.Path.Contains("a.fits"));
        Assert.Equal(1, u.Status.Elsewhere);
        Assert.Contains("anderes Profil", StatusText.Describe(u.Status));
        _context = _context with { Settings = S1 };
        await Drain(u);
        Assert.Contains(_server.Calls, c => c.Path.Contains("a.fits") && c.Auth == "Bearer " + K1);
        Assert.Equal(0, u.Status.Elsewhere);
    }

    [Fact]
    public async Task Another_profile_with_the_same_key_does_not_send_this_profiles_backlog()
    {
        var u = Make();
        u.FrameSaved(Frame("a.fits"), null);
        _context = new CaptureContext("profile-2", S1);
        await Drain(u);
        Assert.Empty(_server.Calls);
        Assert.Equal(1, u.Status.Elsewhere);
    }

    [Fact]
    public async Task Frames_from_before_the_key_go_with_the_key_of_their_own_profile_only()
    {
        _context = new CaptureContext("profile-1", new ClientSettings("https://a.example/", ""));
        var u = Make();
        u.FrameSaved(Frame("early.fits"), null);
        _context = new CaptureContext("profile-2", S2);
        await Drain(u);
        Assert.Empty(_server.Calls);
        _context = new CaptureContext("profile-1", S1);
        await Drain(u);
        Assert.Contains(_server.Calls, c => c.Path.Contains("early.fits") && c.Auth == "Bearer " + K1);
    }

    [Fact]
    public async Task A_key_missing_for_a_moment_keeps_the_order()
    {
        var u = Make();
        u.SequenceStarted("seq");
        u.FrameSaved(Frame("1.fits"), null);
        _context = _context with { Settings = new ClientSettings("https://a.example/", "") };
        u.FrameSaved(Frame("2.fits"), null);
        _context = _context with { Settings = S1 };
        u.FrameSaved(Frame("3.fits"), null);
        u.SequenceFinished();
        await Drain(u);
        Assert.Equal(
            new[]
            {
                "POST /capture/sequences/q1/start",
                "PUT /capture/sequences/q1/frames/1.fits",
                "PUT /capture/sequences/q1/frames/2.fits",
                "PUT /capture/sequences/q1/frames/3.fits",
                "POST /capture/sequences/q1/end",
            },
            _server.Wire);
    }

    [Fact]
    public void A_half_typed_key_is_not_a_key()
    {
        Assert.True(new ClientSettings("https://bortle.eu", K1).IsComplete);
        Assert.False(new ClientSettings("https://bortle.eu", K1[..20]).IsComplete);
        Assert.False(new ClientSettings("https://bortle.eu", "bnp_").IsComplete);
        Assert.False(new ClientSettings("https://bortle.eu", "agent_" + K1[4..]).IsComplete);
        Assert.False(new ClientSettings("bortle.eu", K1).IsComplete);
        Assert.False(new ClientSettings("ftp://bortle.eu", K1).IsComplete);
        Assert.True(new ClientSettings("http://192.168.1.20:3100", K1).IsComplete);
    }

    // --- 2. two N.I.N.A. instances --------------------------------------------------

    [Fact]
    public void Two_instances_never_replace_or_delete_each_others_entries()
    {
        var dir = Path.Combine(_dir, "shared");
        using var first = new FrameQueue(dir);
        using var second = new FrameQueue(dir);
        var a = first.Enqueue(EntryKind.Frame, _now, "s", "a.fits");
        var b = second.Enqueue(EntryKind.Frame, _now, "s", "b.fits");
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, first.Count);
        first.Remove(a);
        Assert.True(second.Contains(b));
        Assert.Equal("b.fits", second.Peek()?.Path);
    }

    [Fact]
    public async Task Only_one_instance_sends_a_queue_and_a_profile_switch_lets_go()
    {
        var one = Make();
        var two = Make();
        one.FrameSaved(Frame("a.fits"), null);
        two.FrameSaved(Frame("b.fits"), null);
        await Drain(one);
        Assert.Equal(Uploader.KeyPause, await two.StepAsync(CancellationToken.None));
        Assert.Equal(UploadProblem.Busy, two.Status.Problem);
        Assert.Single(_server.Calls, c => c.Path.EndsWith("/a.fits"));
        Assert.Single(_server.Calls, c => c.Path.EndsWith("/b.fits"));
        // the first NINA switches to another profile: the second takes over
        var profile = "profile-1";
        var oneOnOther = new CaptureContext("profile-9", S1);
        _context = oneOnOther;
        await one.StepAsync(CancellationToken.None);
        _context = new CaptureContext(profile, S1);
        two.FrameSaved(Frame("c.fits"), null);
        await Drain(two);
        Assert.Contains(_server.Calls, c => c.Path.EndsWith("/c.fits"));
        one.Dispose();
        two.Dispose();
    }

    [Fact]
    public async Task The_pending_queue_is_not_starved_by_another_instance()
    {
        var owner = Make();
        await owner.StepAsync(CancellationToken.None); // holds profile-1 + K1
        var noKey = new CaptureContext("profile-1", new ClientSettings("https://a.example/", ""));
        var saved = _context;
        _context = noKey;
        var capturer = Make();
        capturer.FrameSaved(Frame("pending.fits"), null);
        _context = saved;
        var other = Make();
        for (var i = 0; i < 5; i++)
        {
            await other.StepAsync(CancellationToken.None);
            await owner.StepAsync(CancellationToken.None);
        }
        Assert.Contains(_server.Calls, c => c.Path.EndsWith("pending.fits"));
        owner.Dispose();
        other.Dispose();
        capturer.Dispose();
    }

    // --- 3. the upload switch -------------------------------------------------------

    [Fact]
    public async Task Switching_uploads_off_stops_the_backlog_too_and_after_a_restart()
    {
        var u = Make();
        u.FrameSaved(Frame("a.fits"), null);
        _context = _context with { UploadEnabled = false };
        Assert.Equal(Uploader.DisabledPoll, await u.StepAsync(CancellationToken.None));
        Assert.Equal(UploadProblem.Disabled, u.Status.Problem);
        u.Dispose();
        var again = Make();
        Assert.Equal(Uploader.DisabledPoll, await again.StepAsync(CancellationToken.None));
        Assert.Empty(_server.Calls);
        _context = _context with { UploadEnabled = true };
        await Drain(again);
        Assert.Contains(_server.Calls, c => c.Path.EndsWith("/a.fits"));
    }

    [Fact]
    public async Task Uploads_switched_on_mid_sequence_still_end_that_sequence()
    {
        _context = _context with { UploadEnabled = false };
        var u = Make();
        u.SequenceStarted("seq", queue: false);
        _context = _context with { UploadEnabled = true };
        u.FrameSaved(Frame("1.fits"), null);
        u.SequenceFinished();
        await Drain(u);
        Assert.Equal(new[] { "PUT /capture/sequences/q1/frames/1.fits", "POST /capture/sequences/q1/end" }, _server.Wire);
    }

    // --- 4. which sequence a frame belongs to --------------------------------------

    [Fact]
    public async Task A_late_end_names_its_own_sequence_not_the_next()
    {
        var u = Make();
        u.SequenceStarted("A");
        u.FrameSaved(Frame("a.fits"), null, _now);
        _now += TimeSpan.FromMinutes(1);
        var end = u.EndAfterSavesAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
        u.SequenceStarted("B");
        u.FrameSaved(Frame("b.fits"), null, _now);
        await end;
        await Drain(u);
        Assert.Equal(
            new[]
            {
                "POST /capture/sequences/q1/start",
                "PUT /capture/sequences/q1/frames/a.fits",
                "POST /capture/sequences/q1/end",
                "POST /capture/sequences/q2/start",
                "PUT /capture/sequences/q2/frames/b.fits",
            },
            _server.Wire);
    }

    [Fact]
    public async Task An_image_written_after_the_next_sequence_began_goes_by_its_exposure_time()
    {
        var u = Make();
        u.SequenceStarted("A");
        var exposedInA = _now + TimeSpan.FromMinutes(5);
        _now += TimeSpan.FromMinutes(6);
        u.SequenceFinished();
        u.SequenceStarted("B");                                 // at once
        u.FrameSaved(Frame("A_last.fits"), null, exposedInA);   // written only now
        _now += TimeSpan.FromMinutes(1);
        u.FrameSaved(Frame("B_1.fits"), null, _now - TimeSpan.FromSeconds(30));
        await Drain(u);
        Assert.Contains("PUT /capture/sequences/q1/frames/A_last.fits", _server.Wire);
        Assert.Contains("PUT /capture/sequences/q2/frames/B_1.fits", _server.Wire);
    }

    [Fact]
    public async Task A_snapshot_or_failed_save_does_not_move_the_next_frame()
    {
        // nothing is counted: only the frame's own exposure time decides
        var saves = new SaveTracker(() => _now);
        var u = Make(saves);
        u.SequenceStarted("A");
        saves.SaveStarted(); // never becomes a frame
        u.SequenceStarted("B");
        _now += TimeSpan.FromMinutes(2);
        u.FrameSaved(Frame("B1.fits"), null, _now - TimeSpan.FromSeconds(30));
        await Drain(u);
        Assert.Contains("PUT /capture/sequences/q2/frames/B1.fits", _server.Wire);
    }

    [Fact]
    public async Task Without_an_exposure_time_a_frame_soon_after_the_end_still_joins()
    {
        var u = Make();
        u.SequenceStarted("A");
        u.SequenceFinished();
        _now += TimeSpan.FromSeconds(13);
        u.FrameSaved(Frame("late.fits"), null);
        _now += Uploader.LateFrameWindow + TimeSpan.FromMinutes(1);
        u.FrameSaved(Frame("much_later.fits"), null);
        await Drain(u);
        Assert.Contains("PUT /capture/sequences/q1/frames/late.fits", _server.Wire);
        Assert.Contains("PUT /capture/sequences/q2/frames/much_later.fits", _server.Wire);
    }

    [Fact]
    public async Task Frames_by_hand_share_one_id_until_a_long_pause()
    {
        var u = Make();
        u.FrameSaved(Frame("a.fits"), null);
        _now += TimeSpan.FromHours(1);
        u.FrameSaved(Frame("b.fits"), null);
        _now += Uploader.SessionIdle + TimeSpan.FromHours(1);
        u.FrameSaved(Frame("c.fits"), null);
        await Drain(u);
        Assert.Equal(
            new[]
            {
                "PUT /capture/sequences/q1/frames/a.fits",
                "PUT /capture/sequences/q1/frames/b.fits",
                "PUT /capture/sequences/q2/frames/c.fits",
            },
            _server.Wire);
    }

    [Fact]
    public async Task Ending_twice_sends_one_end_and_one_no_op()
    {
        var u = Make();
        u.SequenceStarted("seq");
        u.SequenceFinished(); // the instruction
        u.SequenceFinished(); // then NINA's own event
        await Drain(u);
        Assert.Single(_server.Wire, c => c.EndsWith("/end"));
    }

    [Fact]
    public async Task A_sequence_that_never_said_it_finished_is_ended_when_the_next_starts()
    {
        var u = Make();
        u.SequenceStarted("A");
        u.FrameSaved(Frame("a.fits"), null);
        u.SequenceStarted("B"); // NINA started B without finishing A
        u.FrameSaved(Frame("b.fits"), null);
        u.SequenceFinished();
        await Drain(u);
        Assert.Contains("POST /capture/sequences/q1/end", _server.Wire);
        Assert.Equal("POST /capture/sequences/q2/end", _server.Wire[^1]);
    }

    [Fact]
    public async Task The_sequence_running_when_NINA_stopped_is_ended_by_the_next_run()
    {
        var u = Make();
        u.SequenceStarted("A");
        u.FrameSaved(Frame("a.fits"), null);
        u.Dispose(); // NINA crashed: no SequenceFinished
        var again = Make();
        await Drain(again);
        Assert.Equal("POST /capture/sequences/q1/end", _server.Wire[^1]);
        again.Dispose();
        var third = Make(); // and only once
        await Drain(third);
        Assert.Single(_server.Wire, c => c.EndsWith("/end"));
    }

    [Fact]
    public async Task Frames_after_a_finish_instruction_mid_sequence_get_their_own_sequence()
    {
        var u = Make();
        u.SequenceStarted("seq");
        u.FrameSaved(Frame("1.fits"), null, _now);
        _now += TimeSpan.FromMinutes(1);
        await u.FinishSessionAsync(TimeSpan.Zero, CancellationToken.None); // the instruction, mid-sequence
        _now += TimeSpan.FromMinutes(5);
        u.FrameSaved(Frame("2.fits"), null, _now); // NINA carries on
        await u.EndAfterSavesAsync(TimeSpan.Zero, CancellationToken.None); // NINA's own finish
        await Drain(u);
        Assert.Equal(
            new[]
            {
                "POST /capture/sequences/q1/start",
                "PUT /capture/sequences/q1/frames/1.fits",
                "POST /capture/sequences/q1/end",
                "POST /capture/sequences/q2/start",
                "PUT /capture/sequences/q2/frames/2.fits",
                "POST /capture/sequences/q2/end",
            },
            _server.Wire);
    }

    // --- crashes, restarts, the PC itself ---------------------------------------------

    [Fact]
    public async Task The_end_is_on_disk_at_once_so_closing_NINA_loses_nothing()
    {
        var saves = new SaveTracker(() => _now);
        var u = Make(saves);
        u.SequenceStarted("seq");
        saves.SaveStarted(); // the last image still on its way
        await u.EndAfterSavesAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
        await u.StopAsync();
        u.Dispose();
        var again = Make();
        await Drain(again);
        Assert.Equal("POST /capture/sequences/q1/end", _server.Wire[^1]);
    }

    [Fact]
    public async Task A_clock_stepped_back_keeps_the_order()
    {
        var u = Make();
        u.SequenceStarted("seq");
        _now -= TimeSpan.FromSeconds(30);
        u.FrameSaved(Frame("1.fits"), null);
        u.SequenceFinished();
        await Drain(u);
        Assert.Equal(
            new[] { "POST /capture/sequences/q1/start", "PUT /capture/sequences/q1/frames/1.fits", "POST /capture/sequences/q1/end" },
            _server.Wire);
    }

    [Fact]
    public async Task After_a_restart_with_a_corrected_clock_the_old_backlog_still_goes_first()
    {
        var u = Make();
        u.SequenceStarted("A");
        u.FrameSaved(Frame("A1.fits"), null);
        u.Dispose();
        // a fresh process with the clock an hour behind (it was ahead before)
        typeof(FrameQueue).GetField("s_lastTicks", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 0L);
        _now -= TimeSpan.FromHours(1);
        var again = Make();
        again.SequenceStarted("B");
        again.FrameSaved(Frame("B1.fits"), null);
        await Drain(again);
        Assert.Equal(
            new[]
            {
                "POST /capture/sequences/q1/start",
                "PUT /capture/sequences/q1/frames/A1.fits",
                "POST /capture/sequences/q1/end", // A never finished: ended by the new run
                "POST /capture/sequences/q2/start",
                "PUT /capture/sequences/q2/frames/B1.fits",
            },
            _server.Wire);
    }

    [Fact]
    public async Task A_deleted_queue_folder_neither_kills_the_loop_nor_loses_later_frames()
    {
        var u = Make();
        u.FrameSaved(Frame("a.fits"), null);
        u.Start();
        try
        {
            await Task.Delay(200);
            Directory.Delete(Path.Combine(_dir, "queues"), true);
            u.Wake();
            await Task.Delay(200);
            var loop = (Task?)typeof(Uploader).GetField("_loop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(u);
            Assert.False(loop!.IsCompleted, $"the loop ended: {loop.Exception}");
            u.FrameSaved(Frame("b.fits"), null);
            Assert.True(await Eventually(() => _server.Calls.Any(c => c.Path.EndsWith("/b.fits"))));
        }
        finally
        {
            await u.StopAsync();
        }
    }

    [Fact]
    public async Task A_frame_on_an_unreachable_folder_waits_and_a_deleted_file_is_dropped()
    {
        var u = Make();
        u.FrameSaved(Frame("share.fits", "nas"), null);
        var gone = Frame("gone.fits");
        u.FrameSaved(gone, null);
        File.Delete(gone);
        var nas = Path.Combine(_dir, "nas");
        Directory.Move(nas, nas + "-offline");
        await u.StepAsync(CancellationToken.None);
        Assert.Equal(UploaderState.Retrying, u.Status.State);
        Assert.Equal(2, u.Waiting);
        Directory.Move(nas + "-offline", nas);
        await Drain(u);
        Assert.Contains(_server.Calls, c => c.Path.EndsWith("/share.fits"));
        Assert.DoesNotContain(_server.Calls, c => c.Path.EndsWith("/gone.fits"));
        Assert.Equal(0, u.Waiting);
    }

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

    // --- finishing, waking, the status line ---------------------------------------

    [Fact]
    public async Task Finishing_waits_for_the_sequence_to_arrive()
    {
        var u = Make();
        u.SequenceStarted("seq");
        u.FrameSaved(Frame("a.fits"), null);
        u.Start();
        try
        {
            Assert.True(await u.FinishSessionAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
            Assert.Equal("POST /capture/sequences/q1/end", _server.Wire[^1]);
        }
        finally
        {
            await u.StopAsync();
        }
    }

    [Fact]
    public async Task Finishing_waits_for_the_image_NINA_is_still_saving()
    {
        var saves = new SaveTracker(() => _now);
        var u = Make(saves);
        u.SequenceStarted("seq");
        var exposed = _now;
        saves.SaveStarted();
        u.Start();
        try
        {
            var finish = u.FinishSessionAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
            await Task.Delay(100);
            Assert.False(finish.IsCompleted);
            _now += TimeSpan.FromSeconds(13);
            u.FrameSaved(Frame("dark.fits"), null, exposed);
            saves.SaveFinished();
            _now += SaveTracker.DefaultQuiet;
            Assert.True(await finish);
            Assert.Contains("PUT /capture/sequences/q1/frames/dark.fits", _server.Wire);
        }
        finally
        {
            await u.StopAsync();
        }
    }

    [Fact]
    public async Task Finishing_does_not_hold_the_sequence_while_nothing_can_be_sent()
    {
        _context = _context with { UploadEnabled = false };
        var u = Make();
        u.SequenceStarted("seq", queue: false);
        u.Start();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(await u.FinishSessionAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"held the sequence for {sw.Elapsed}");
        }
        finally
        {
            await u.StopAsync();
        }
    }

    [Fact]
    public async Task A_new_frame_wakes_an_idle_uploader()
    {
        var u = Make();
        u.Start();
        Assert.True(await Eventually(() => u.Status.State == UploaderState.Idle));
        u.FrameSaved(Frame("a.fits"), null);
        Assert.True(await Eventually(() => u.Waiting == 0));
        await u.StopAsync();
    }

    [Fact]
    public async Task A_successful_connection_test_ends_the_pause_at_once()
    {
        var u = Make();
        u.Start();
        Assert.True(await Eventually(() => u.Status.State == UploaderState.Idle));
        _server.Script.Enqueue(HttpStatusCode.Unauthorized);
        u.FrameSaved(Frame("a.fits"), null);
        Assert.True(await Eventually(() => u.Status.Problem == UploadProblem.KeyRejected));
        await Task.Delay(200);
        Assert.Equal(1, u.Waiting);
        u.ConnectionConfirmed();
        Assert.True(await Eventually(() => u.Waiting == 0));
        await u.StopAsync();
    }

    [Fact]
    public async Task Typing_a_key_sends_one_request_after_the_typing()
    {
        var u = Make();
        u.Start();
        Assert.True(await Eventually(() => u.Status.State == UploaderState.Idle));
        _server.Script.Enqueue(HttpStatusCode.Unauthorized);
        u.FrameSaved(Frame("a.fits"), null);
        Assert.True(await Eventually(() => u.Status.Problem == UploadProblem.KeyRejected));
        var before = _server.Calls.Count;
        for (var i = 0; i < 10; i++)
        {
            u.SettingsChanged();
            await Task.Delay(30);
        }
        Assert.Equal(before, _server.Calls.Count);
        Assert.True(await Eventually(() => u.Waiting == 0));
        Assert.Equal(before + 1, _server.Calls.Count);
        await u.StopAsync();
        u.Dispose();
    }

    [Fact]
    public void The_status_line_names_problems_plainly()
    {
        Assert.Contains(StatusText.KeyMissing, StatusText.Describe(new UploaderStatus(UploaderState.Paused, 2, null, null, null, UploadProblem.KeyMissing)));
        Assert.Contains("ausgeschaltet", StatusText.Describe(new UploaderStatus(UploaderState.Paused, 2, null, null, null, UploadProblem.Disabled)));
        Assert.Contains("andere N.I.N.A.", StatusText.Describe(new UploaderStatus(UploaderState.Paused, 2, null, null, null, UploadProblem.Busy)));
        var legacy = StatusText.Describe(new UploaderStatus(UploaderState.Idle, 0, null, null, null, Legacy: 3));
        Assert.Contains("3 Einträge einer älteren Plugin-Version", legacy);
    }

    [Fact]
    public void A_rejected_key_is_announced_once_until_uploads_work_again()
    {
        var warn = new KeyWarning();
        var rejected = new UploaderStatus(UploaderState.Paused, 1, null, null, null, UploadProblem.KeyRejected);
        Assert.True(warn.ShouldWarn(rejected));
        Assert.False(warn.ShouldWarn(rejected));
        Assert.True(warn.ShouldWarn(rejected with { LastSent = DateTimeOffset.UtcNow }));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, Outcome.Ok)]
    [InlineData(HttpStatusCode.Unauthorized, Outcome.KeyRejected)]
    [InlineData(HttpStatusCode.Forbidden, Outcome.KeyRejected)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, Outcome.Pause)]
    [InlineData(HttpStatusCode.NotFound, Outcome.Retry)]
    [InlineData(HttpStatusCode.BadRequest, Outcome.Drop)]
    [InlineData(HttpStatusCode.TooManyRequests, Outcome.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, Outcome.Retry)]
    public void Answers_are_classified(HttpStatusCode status, Outcome expected) =>
        Assert.Equal(expected, BortleClient.Classify(status));

    // --- review round 5 ---------------------------------------------------------------

    [Fact]
    public async Task A_second_NINA_never_ends_the_sequence_the_first_is_running()
    {
        var a = Make();
        a.SequenceStarted("A");
        a.FrameSaved(Frame("1.fits"), null);
        using (var b = Make()) // a second NINA starts on the same machine
        {
            await Drain(a);
            Assert.DoesNotContain(_server.Wire, w => w.EndsWith("/end"));
        }
        a.FrameSaved(Frame("2.fits"), null);
        a.SequenceFinished();
        await Drain(a);
        Assert.Equal(
            new[]
            {
                "POST /capture/sequences/q1/start",
                "PUT /capture/sequences/q1/frames/1.fits",
                "PUT /capture/sequences/q1/frames/2.fits",
                "POST /capture/sequences/q1/end",
            },
            _server.Wire);
        a.Dispose();
    }

    [Fact]
    public async Task Two_NINAs_each_end_their_own_sequence_after_a_crash()
    {
        var a = Make();
        a.SequenceStarted("A");
        var b = Make();
        b.SequenceStarted("B");
        a.Dispose(); // both crash
        b.Dispose();
        using var again = Make();
        await Drain(again);
        Assert.Equal(2, _server.Wire.Count(w => w.EndsWith("/end")));
        Assert.Contains("POST /capture/sequences/q1/end", _server.Wire);
        Assert.Contains("POST /capture/sequences/q2/end", _server.Wire);
    }

    [Fact]
    public async Task The_end_goes_to_the_account_that_got_the_start()
    {
        var u = Make();
        u.SequenceStarted("A");
        u.FrameSaved(Frame("1.fits"), null);
        await Drain(u);
        _context = _context with { Settings = S2 }; // key changed mid-sequence
        u.SequenceFinished();
        await Drain(u);
        Assert.DoesNotContain(_server.Calls, c => c.Path.EndsWith("/end") && c.Auth == "Bearer " + K2);
        _context = _context with { Settings = S1 };
        await Drain(u);
        Assert.Single(_server.Calls, c => c.Path.EndsWith("/end"));
        Assert.Contains(_server.Calls, c => c.Path.EndsWith("/end") && c.Auth == "Bearer " + K1);
        u.Dispose();
    }

    [Fact]
    public async Task An_unfinished_sequence_ends_with_the_account_that_got_its_start()
    {
        var u = Make();
        u.SequenceStarted("A");
        _context = new CaptureContext("profile-2", S2); // another profile
        u.SequenceStarted("B"); // A never said it finished
        u.SequenceFinished();
        await Drain(u);
        // profile 2 hears only of B: its start and its end
        Assert.Equal(
            new[] { "POST /capture/sequences/q1/start", "POST /capture/sequences/q1/end" },
            _server.Wire);
        _context = new CaptureContext("profile-1", S1);
        await Drain(u);
        // A's start and end go to profile 1, with its key
        Assert.Equal("POST /capture/sequences/q2/end", _server.Wire[^1]);
        Assert.Equal("Bearer " + K1, _server.Calls[^1].Auth);
        Assert.Contains("POST /capture/sequences/q2/start", _server.Wire);
        u.Dispose();
    }
}
