using System.Net;
using Xunit;

namespace Bortle.Capture.Tests;

/// <summary>
/// The four findings of the N.I.N.A. plugin manifest review (PR #795,
/// 2026-11-17), each reproduced as it was reported and then held.
/// </summary>
public sealed class QueueOwnershipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bortle-own-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServer _server = new();
    private readonly NullLog _log = new();
    private DateTimeOffset _now = new(2026, 11, 17, 20, 0, 0, TimeSpan.Zero);
    private static readonly ClientSettings AccountA = new("https://a.example/", "bnp_key_aAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    private static readonly ClientSettings AccountB = new("https://b.example/", "bnp_key_bBBBBBBBBBBBBBBBBBBBBBBBBBBB");
    private CaptureContext _context = new("profile-1", AccountA);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Uploader Make(SaveTracker? saves = null) =>
        new(
            new QueueStore(Path.Combine(_dir, "queues")),
            new BortleClient(new HttpClient(_server), () => _context.Settings),
            () => _context,
            _log,
            () => _now,
            saves);

    private string Frame(string name)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "frames"));
        var path = Path.Combine(_dir, "frames", name);
        File.WriteAllBytes(path, new byte[2880]);
        return path;
    }

    private static async Task Drain(Uploader uploader, int maxSteps = 30)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            var wait = await uploader.StepAsync(CancellationToken.None);
            if (wait is not null && uploader.Status.State != UploaderState.Sending) return;
        }
    }

    // --- 1. a backlog stays with its owner ------------------------------------------

    [Fact]
    public async Task A_frame_queued_for_account_A_is_never_sent_to_account_B()
    {
        var uploader = Make();
        uploader.FrameSaved(Frame("a.fits"), "M 31");
        // the settings change to account B before anything went out
        _context = _context with { Settings = AccountB };
        uploader.FrameSaved(Frame("b.fits"), "M 31");
        await Drain(uploader);

        Assert.All(_server.Calls, c => Assert.Equal("Bearer bnp_key_bBBBBBBBBBBBBBBBBBBBBBBBBBBB", c.Auth));
        Assert.DoesNotContain(_server.Calls, c => c.Path.Contains("a.fits"));
        Assert.Contains(_server.Calls, c => c.Path.Contains("b.fits"));
        Assert.Equal(0, uploader.Waiting);
        Assert.Equal(1, uploader.Status.Elsewhere); // A's frame, waiting for A
        Assert.Contains("anderes Profil", StatusText.Describe(uploader.Status));

        // back to account A: its frame goes to A, with A's key
        _context = _context with { Settings = AccountA };
        var before = _server.Calls.Count;
        await Drain(uploader);
        var sent = _server.Calls.Skip(before).ToList();
        Assert.Contains(sent, c => c.Path.Contains("a.fits"));
        Assert.All(sent, c => Assert.Equal("Bearer bnp_key_aAAAAAAAAAAAAAAAAAAAAAAAAAAA", c.Auth));
        Assert.Equal(0, uploader.Status.Elsewhere);
    }

    [Fact]
    public async Task Another_profile_does_not_send_this_profiles_backlog()
    {
        var uploader = Make();
        uploader.FrameSaved(Frame("a.fits"), null);
        // another NINA profile with the SAME server and key: still not its frames
        _context = new CaptureContext("profile-2", AccountA);
        await Drain(uploader);
        Assert.Empty(_server.Calls);
        Assert.Equal(1, uploader.Status.Elsewhere);
    }

    [Fact]
    public async Task A_session_belongs_to_its_destination()
    {
        var uploader = Make();
        uploader.SequenceStarted("night");
        uploader.FrameSaved(Frame("a1.fits"), null);
        await Drain(uploader); // s1 on A
        _context = _context with { Settings = AccountB };
        uploader.FrameSaved(Frame("b1.fits"), null);
        await Drain(uploader);
        // B never hears of A's session id: it gets a session of its own
        var toB = _server.Calls.Where(c => c.Auth == "Bearer bnp_key_bBBBBBBBBBBBBBBBBBBBBBBBBBBB").Select(c => c.Path).ToList();
        Assert.Equal(new[] { "/capture/sessions", "/capture/sessions/s2/frames/b1.fits" }, toB);
    }

    [Fact]
    public async Task Frames_from_before_the_key_go_to_the_first_key_of_the_same_profile_only()
    {
        _context = new CaptureContext("profile-1", new ClientSettings("https://a.example/", ""));
        var uploader = Make();
        uploader.FrameSaved(Frame("early.fits"), null);
        _context = new CaptureContext("profile-2", AccountB); // someone else's profile
        await Drain(uploader);
        Assert.Empty(_server.Calls);
        _context = new CaptureContext("profile-1", AccountA); // the key arrives in its own profile
        await Drain(uploader);
        Assert.Contains(_server.Calls, c => c.Path.Contains("early.fits") && c.Auth == "Bearer bnp_key_aAAAAAAAAAAAAAAAAAAAAAAAAAAA");
    }

    // --- 2. two N.I.N.A. instances --------------------------------------------------

    [Fact]
    public void Two_instances_never_replace_or_delete_each_others_entries()
    {
        var dir = Path.Combine(_dir, "shared");
        using var first = new FrameQueue(dir);
        using var second = new FrameQueue(dir); // another process, same directory
        var a = first.Enqueue(EntryKind.Frame, _now, "s", "a.fits");
        var b = second.Enqueue(EntryKind.Frame, _now, "s", "b.fits"); // same tick, both "first"
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(2, first.Count);

        first.Remove(a);
        Assert.True(second.Contains(b));
        Assert.Equal("b.fits", second.Peek()?.Path);
    }

    [Fact]
    public async Task Only_one_instance_sends_a_queue()
    {
        var one = Make();
        var two = Make(); // a second NINA on the same profile and key
        one.FrameSaved(Frame("a.fits"), null);
        two.FrameSaved(Frame("b.fits"), null);
        await Drain(one);
        Assert.Equal(TimeSpan.FromSeconds(30), await two.StepAsync(CancellationToken.None));
        Assert.Equal(UploadProblem.Busy, two.Status.Problem);
        // the owner sent both, each once
        Assert.Single(_server.Calls, c => c.Path.EndsWith("/a.fits"));
        Assert.Single(_server.Calls, c => c.Path.EndsWith("/b.fits"));
        one.Dispose(); // the first NINA closes: the second takes over
        two.FrameSaved(Frame("c.fits"), null);
        await Drain(two);
        Assert.Contains(_server.Calls, c => c.Path.EndsWith("/c.fits"));
        two.Dispose();
    }

    // --- 3. the upload switch governs the backlog -----------------------------------

    [Fact]
    public async Task Switching_uploads_off_stops_the_backlog_too_and_after_a_restart()
    {
        var uploader = Make();
        uploader.FrameSaved(Frame("a.fits"), null);
        _context = _context with { UploadEnabled = false };
        Assert.Equal(Uploader.DisabledPoll, await uploader.StepAsync(CancellationToken.None));
        Assert.Equal(UploadProblem.Disabled, uploader.Status.Problem);
        Assert.Empty(_server.Calls);
        uploader.Dispose();

        var again = Make(); // NINA restarted, still switched off
        Assert.Equal(Uploader.DisabledPoll, await again.StepAsync(CancellationToken.None));
        Assert.Empty(_server.Calls);
        Assert.Equal(1, again.Waiting);

        _context = _context with { UploadEnabled = true };
        await Drain(again);
        Assert.Contains(_server.Calls, c => c.Path.EndsWith("/a.fits"));
    }

    // --- 4. a delayed end closes its own session ------------------------------------

    [Fact]
    public async Task A_delayed_end_closes_its_own_session_not_the_next_one()
    {
        var saves = new SaveTracker(() => _now);
        var uploader = Make(saves);
        uploader.Poll = TimeSpan.FromMilliseconds(5);
        uploader.SequenceStarted("A");
        uploader.FrameSaved(Frame("a.fits"), null);
        saves.SaveStarted(); // NINA is still writing A's last image
        var endOfA = uploader.EndAfterSavesAsync(TimeSpan.FromMinutes(10), CancellationToken.None);

        uploader.SequenceStarted("B"); // B begins while A's end waits
        uploader.FrameSaved(Frame("a_last.fits"), null); // A's last image lands now
        saves.SaveFinished();
        uploader.FrameSaved(Frame("b.fits"), null);
        _now += SaveTracker.DefaultQuiet + TimeSpan.FromSeconds(1);
        await endOfA;
        await Drain(uploader);

        var paths = _server.Calls.Select(c => c.Path).ToList();
        Assert.Equal(
            new[]
            {
                "/capture/sessions",
                "/capture/sessions/s1/frames/a.fits",
                "/capture/sessions",
                "/capture/sessions/s1/frames/a_last.fits",
                "/capture/sessions/s2/frames/b.fits",
                "/capture/sessions/s1/end",
            },
            paths);
        Assert.Equal("s2", uploader.Status.SessionId); // B is still open
    }

    [Fact]
    public async Task The_finish_instruction_ends_the_session_that_was_running_when_it_began()
    {
        var saves = new SaveTracker(() => _now);
        var uploader = Make(saves);
        uploader.Poll = TimeSpan.FromMilliseconds(5);
        uploader.SequenceStarted("A");
        saves.SaveStarted();
        uploader.Start();
        try
        {
            var finish = uploader.FinishSessionAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
            uploader.SequenceStarted("B");
            saves.SaveFinished();
            _now += SaveTracker.DefaultQuiet + TimeSpan.FromSeconds(1);
            Assert.True(await finish);
            Assert.Equal("/capture/sessions/s1/end", _server.Calls[^1].Path);
            Assert.DoesNotContain(_server.Calls, c => c.Path == "/capture/sessions/s2/end");
        }
        finally
        {
            await uploader.StopAsync();
        }
    }
}
