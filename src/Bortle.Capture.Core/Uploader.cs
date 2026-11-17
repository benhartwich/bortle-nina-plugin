namespace Bortle.Capture;

public interface ILog
{
    void Info(string message);
    void Error(string message);
}

public enum UploaderState
{
    Idle,
    Sending,
    /// <summary>A failure; the same entry is tried again after a pause.</summary>
    Retrying,
    /// <summary>Waiting for a person: wrong key, quota full, not set up.</summary>
    Paused,
    Stopped,
}

/// <summary>What a person has to fix, when the uploader is paused for one.</summary>
public enum UploadProblem
{
    None,
    /// <summary>No server address or key in the active NINA profile.</summary>
    KeyMissing,
    /// <summary>The server refused the key.</summary>
    KeyRejected,
    /// <summary>The account's storage is full.</summary>
    Quota,
    /// <summary>Uploads are switched off in this profile; nothing is sent, the queue waits.</summary>
    Disabled,
    /// <summary>Another N.I.N.A. instance is sending this queue.</summary>
    Busy,
}

public sealed record UploaderStatus(
    UploaderState State,
    int Waiting,
    string? Message,
    DateTimeOffset? LastSent,
    string? SessionId,
    UploadProblem Problem = UploadProblem.None,
    int Elsewhere = 0);

/// <summary>
/// Sends the queue to Bortle, one entry after the other, in a background task.
///
/// It never blocks NINA: the event handlers only enqueue. A failure leaves the
/// entry at the head and waits — 10 s, doubling up to 5 min — so a night with
/// the server down loses nothing and the frames follow when it is back. A
/// refusal a person has to fix (wrong key, quota) pauses until the settings
/// change or a connection test succeeds. Frames the server will never take
/// are skipped and logged.
///
/// A key problem pauses only briefly (<see cref="KeyPause"/>): the key lives
/// in the ACTIVE NINA profile and is read again on every step, so a profile
/// switch or a key pasted a moment later must not leave a night's frames
/// waiting for minutes — that is what made "Verbindung testen" look like the
/// only way to get uploads going again (2026-11-12). A full quota needs a
/// person and keeps the long pause.
/// </summary>
public sealed class Uploader : IDisposable
{
    /// <summary>The server closes a session after this long without a frame;
    /// a session we have not used for as long is last night's.</summary>
    public static readonly TimeSpan SessionIdle = TimeSpan.FromHours(6);

    /// <summary>
    /// A frame saved this long after its session ended still goes to that
    /// session: NINA writes the last image of a sequence in the background,
    /// after the sequence (and "Finish Bortle session") moved on.
    /// </summary>
    public static readonly TimeSpan LateFrameWindow = TimeSpan.FromMinutes(30);

    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PauseFor = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan KeyPause = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Quiet time after the last settings change before the uploader looks
    /// again. The option fields save on every keystroke; waking on each would
    /// send one request per typed character — every one a 401 with half a key.
    /// </summary>
    internal TimeSpan SettingsSettle { get; set; } = TimeSpan.FromSeconds(1.5);

    private Timer? _settle;

    private readonly QueueStore _store;
    private readonly BortleClient _client;
    private readonly Func<CaptureContext> _context;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SaveTracker? _saves;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sessions = new();
    private Task? _loop;
    private TimeSpan _retry = FirstRetry;
    private UploaderStatus _status;

    // The logical session of the sequence running now, the one that ended
    // last (a frame NINA saves just after the end still belongs to it), and
    // the one for frames taken outside any sequence.
    private string? _current;
    private (string Id, DateTimeOffset At)? _lastEnded;
    private (string Id, DateTimeOffset LastUsed)? _adhoc;

    public event Action? StatusChanged;

    public Uploader(
        QueueStore store,
        BortleClient client,
        Func<CaptureContext> context,
        ILog log,
        Func<DateTimeOffset>? clock = null,
        SaveTracker? saves = null)
    {
        _store = store;
        _client = client;
        _context = context;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _saves = saves;
        // Not "Idle" before the loop has looked: nothing is known yet.
        _status = new UploaderStatus(UploaderState.Stopped, 0, null, null, null);
        _store.Changed += Wake;
    }

    public UploaderStatus Status => _status;

    /// <summary>How long NINA must be quiet before its last image counts as saved.</summary>
    internal TimeSpan SaveQuiet { get; set; } = SaveTracker.DefaultQuiet;

    internal TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(500);

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>
    /// The server just accepted the key (a connection test): forget any
    /// backoff or pause and send what is waiting now.
    /// </summary>
    public void ConnectionConfirmed()
    {
        _retry = FirstRetry;
        if (_status.Problem is UploadProblem.KeyMissing or UploadProblem.KeyRejected)
        {
            Publish(UploaderState.Idle, null);
        }
        Wake();
    }

    /// <summary>
    /// The server address or key changed (possibly mid-typing): look at the
    /// queue once the field has been quiet for <see cref="SettingsSettle"/>.
    /// </summary>
    public void SettingsChanged()
    {
        lock (_wake)
        {
            // never throw on the timer thread: that would take NINA down
            _settle ??= new Timer(_ =>
            {
                try
                {
                    Wake();
                }
                catch (ObjectDisposedException)
                {
                }
            });
            _settle.Change(SettingsSettle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Look at the queue now (something was added, or the settings changed).</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>Entries waiting for the active profile and its server and key.</summary>
    public int Waiting
    {
        get
        {
            var context = _context();
            var pending = _store.Pending(context.ProfileId).Count;
            return context.Settings.IsComplete
                ? pending + _store.Destination(context.ProfileId, context.Settings).Count
                : pending;
        }
    }

    public void SequenceStarted(string? sequence)
    {
        string session;
        lock (_sessions)
        {
            session = NewSession();
            _current = session;
            _lastEnded = null;
        }
        Enqueue(EntryKind.SessionStart, session, sequence: sequence);
    }

    public void FrameSaved(string path, string? target) =>
        Enqueue(EntryKind.Frame, SessionForFrame(), path, target);

    /// <summary>Queue the end of the sequence running now (nothing to end: a no-op entry).</summary>
    public (FrameQueue Queue, QueueEntry Entry) SequenceFinished() => EnqueueEnd(TakeCurrent());

    /// <summary>
    /// Report the end of the session once NINA has saved every image it still
    /// had on its way and every frame before the end has gone, and wait for
    /// that — at most <paramref name="timeout"/> for both together. True when
    /// the end reached the server. On a timeout the end stays queued and
    /// follows the frames later, and an image saved after it still joins the
    /// session (<see cref="LateFrameWindow"/>); nothing is lost either way.
    /// </summary>
    public async Task<bool> FinishSessionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _clock() + timeout;
        // The session is the one running NOW; whatever starts while we wait
        // is another one and must not be ended by this.
        var session = TakeCurrent();
        var settled = await WaitForSavesAsync(deadline, ct).ConfigureAwait(false);
        var (queue, end) = EnqueueEnd(session);
        while (queue.Contains(end))
        {
            if (_clock() >= deadline)
            {
                return false;
            }
            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }
        return settled;
    }

    /// <summary>
    /// NINA's own "sequence finished": queue the end once its last images are
    /// saved (at most <paramref name="timeout"/>), without holding NINA up.
    /// The session is fixed now, so a delayed end never closes the next one.
    /// </summary>
    public async Task EndAfterSavesAsync(TimeSpan timeout, CancellationToken ct)
    {
        var session = TakeCurrent();
        await WaitForSavesAsync(_clock() + timeout, ct).ConfigureAwait(false);
        EnqueueEnd(session);
    }

    private (FrameQueue, QueueEntry) EnqueueEnd(string? session) => Enqueue(EntryKind.SessionEnd, session);

    private (FrameQueue, QueueEntry) Enqueue(
        EntryKind kind, string? session, string? path = null, string? target = null, string? sequence = null)
    {
        // The owner is fixed here, from the profile and settings active NOW.
        var queue = _store.For(_context());
        return (queue, queue.Enqueue(kind, _clock(), session, path, target, sequence));
    }

    /// <summary>End the running session: it becomes the "last ended" one.</summary>
    private string? TakeCurrent()
    {
        lock (_sessions)
        {
            var session = _current;
            if (session is not null)
            {
                _current = null;
                _lastEnded = (session, _clock());
            }
            return session;
        }
    }

    private string SessionForFrame()
    {
        lock (_sessions)
        {
            var now = _clock();
            if (_current is { } current) return current;
            if (_lastEnded is { } ended && now - ended.At <= LateFrameWindow) return ended.Id;
            // A frame outside any sequence (taken by hand, or the plugin came
            // up mid-night): one session for such frames until they pause for
            // as long as the server keeps a session open.
            if (_adhoc is not { } adhoc || now - adhoc.LastUsed > SessionIdle)
            {
                adhoc = (NewSession(), now);
            }
            _adhoc = (adhoc.Id, now);
            return adhoc.Id;
        }
    }

    private static string NewSession() => Guid.NewGuid().ToString("N");

    /// <summary>True when NINA settled before the deadline.</summary>
    private async Task<bool> WaitForSavesAsync(DateTimeOffset deadline, CancellationToken ct)
    {
        if (_saves is null)
        {
            return true;
        }
        var since = _clock();
        while (!_saves.Settled(since, SaveQuiet))
        {
            if (_clock() >= deadline)
            {
                _log.Error($"Bortle: {_saves.InFlight} image(s) still being saved; ending the session anyway");
                return false;
            }
            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }
        return true;
    }

    public async Task StopAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        Publish(UploaderState.Stopped, _status.Message);
    }

    public void Dispose()
    {
        _store.Changed -= Wake;
        _settle?.Dispose();
        _store.Dispose();
        _stop.Cancel();
        _stop.Dispose();
        _wake.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan? wait;
            try
            {
                wait = await StepAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // The loop must outlive anything; a bug costs a minute, not the night.
                _log.Error($"Bortle upload: {e}");
                Publish(UploaderState.Retrying, e.Message);
                wait = TimeSpan.FromMinutes(1);
            }
            if (wait is { } delay)
            {
                try
                {
                    await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Handle the head of the queue once. Returns how long to wait before the
    /// next step, or null to carry straight on. A wake cuts any wait short.
    ///
    /// Only the active profile's queues are looked at, and of those only the
    /// one for the server and key it has now (plus what it queued before it
    /// had a key). Everything else waits for its own owner.
    /// </summary>
    internal async Task<TimeSpan?> StepAsync(CancellationToken ct)
    {
        var context = _context();
        var pending = _store.Pending(context.ProfileId);
        var destination = context.Settings.IsComplete
            ? _store.Destination(context.ProfileId, context.Settings)
            : null;
        _view = (pending, destination);
        if (!context.UploadEnabled)
        {
            // The switch governs sending, not only queuing: a backlog from
            // before it was switched off stays where it is.
            Publish(UploaderState.Paused, null, UploadProblem.Disabled);
            return DisabledPoll;
        }
        if (destination is null)
        {
            if (pending.Count == 0)
            {
                Publish(UploaderState.Idle, null);
                return TimeSpan.FromMinutes(1);
            }
            Publish(UploaderState.Paused, "Bortle server address or plugin key is missing", UploadProblem.KeyMissing);
            return KeyPause;
        }
        if (!destination.TryOwn() || !pending.TryOwn())
        {
            Publish(UploaderState.Paused, "another N.I.N.A. is sending this queue", UploadProblem.Busy);
            return KeyPause;
        }
        var queue = pending;
        var entry = pending.Peek();
        if (entry is null)
        {
            queue = destination;
            entry = destination.Peek();
        }
        if (entry is null)
        {
            Publish(UploaderState.Idle, null);
            return TimeSpan.FromMinutes(1);
        }
        Publish(UploaderState.Sending, Describe(entry));
        var (outcome, message) = await HandleAsync(entry, destination, _client.With(context.Settings), ct)
            .ConfigureAwait(false);
        switch (outcome)
        {
            case Outcome.Ok:
                queue.Remove(entry);
                _retry = FirstRetry;
                _status = _status with { LastSent = _clock() };
                Publish(UploaderState.Sending, null);
                return null;
            case Outcome.Drop:
                _log.Error($"Bortle upload: skipped {Describe(entry)}: {message}");
                queue.Remove(entry);
                Publish(UploaderState.Sending, $"skipped {Describe(entry)}: {message}");
                return null;
            case Outcome.SessionGone:
                // The server forgot the session; the next step opens a new one
                // for the same entry.
                return null;
            case Outcome.KeyMissing:
                Publish(UploaderState.Paused, message, UploadProblem.KeyMissing);
                return KeyPause;
            case Outcome.KeyRejected:
                Publish(UploaderState.Paused, message, UploadProblem.KeyRejected);
                return KeyPause;
            case Outcome.Pause:
                Publish(UploaderState.Paused, message, UploadProblem.Quota);
                return PauseFor;
            default:
                Publish(UploaderState.Retrying, message);
                var wait = _retry;
                _retry = TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, MaxRetry.Ticks));
                return wait;
        }
    }

    /// <summary>How often a profile with uploads switched off looks again (a wake cuts it short).</summary>
    internal static readonly TimeSpan DisabledPoll = TimeSpan.FromMinutes(1);

    private (FrameQueue Pending, FrameQueue? Destination)? _view;

    private async Task<(Outcome, string?)> HandleAsync(
        QueueEntry entry, FrameQueue sessions, BortleClient client, CancellationToken ct)
    {
        var now = _clock();
        var logical = entry.Session;
        var state = logical is null ? null : sessions.GetSession(logical);
        switch (entry.Kind)
        {
            case EntryKind.SessionStart:
            {
                var started = await client.StartSessionAsync(entry.Sequence, ct).ConfigureAwait(false);
                if (started.Outcome == Outcome.Ok && started.Value is { } info && logical is not null)
                {
                    sessions.PutSession(logical, new SessionState(info.Id, now), now);
                    _log.Info($"Bortle session {info.Id} (develop level {info.Level})");
                }
                return (started.Outcome, started.Message);
            }
            case EntryKind.Frame:
            {
                if (entry.Path is null || !File.Exists(entry.Path))
                {
                    return (Outcome.Drop, "the file is gone");
                }
                if (logical is null)
                {
                    logical = NewSession(); // an entry from before sessions had names
                }
                if (state is { Ended: { } endedAt })
                {
                    if (now - endedAt <= LateFrameWindow)
                    {
                        // Saved after its session ended: it still belongs there.
                        var late = await client.PutFrameAsync(state.Id, entry.Path, entry.Target, ct)
                            .ConfigureAwait(false);
                        if (late.Outcome != Outcome.SessionGone)
                        {
                            if (late.Outcome == Outcome.Ok)
                            {
                                _log.Info($"Bortle: {Describe(entry)} joined session {state.Id} after its end");
                            }
                            return (late.Outcome, late.Message);
                        }
                    }
                    state = null; // too late, or the server no longer knows it
                }
                else if (state is not null && now - state.LastUsed > SessionIdle)
                {
                    state = null; // the server has closed it by now
                }
                if (state is null)
                {
                    // No server session for this logical one (yet, or any more):
                    // open one.
                    var started = await client.StartSessionAsync(null, ct).ConfigureAwait(false);
                    if (started.Outcome != Outcome.Ok || started.Value is null)
                    {
                        return (started.Outcome == Outcome.SessionGone ? Outcome.Retry : started.Outcome,
                            started.Message);
                    }
                    state = new SessionState(started.Value.Id, now);
                    sessions.PutSession(logical, state, now);
                }
                var sent = await client.PutFrameAsync(state.Id, entry.Path, entry.Target, ct).ConfigureAwait(false);
                if (sent.Outcome == Outcome.Ok)
                {
                    sessions.PutSession(logical, state with { LastUsed = now }, now);
                }
                else if (sent.Outcome == Outcome.SessionGone)
                {
                    sessions.PutSession(logical, null, now);
                }
                return (sent.Outcome, sent.Message);
            }
            case EntryKind.SessionEnd:
            {
                if (logical is null || state is null || state.Ended is not null)
                {
                    // nothing running, never reached the server, or ended already
                    return (Outcome.Ok, null);
                }
                var ended = await client.EndSessionAsync(state.Id, ct).ConfigureAwait(false);
                if (ended.Outcome is Outcome.Ok or Outcome.SessionGone)
                {
                    sessions.PutSession(
                        logical, ended.Outcome == Outcome.Ok ? state with { Ended = now } : null, now);
                    _log.Info($"Bortle session {state.Id} ended");
                    return (Outcome.Ok, null);
                }
                return (ended.Outcome, ended.Message);
            }
            default:
                return (Outcome.Drop, $"unknown entry {entry.Kind}");
        }
    }

    private static string Describe(QueueEntry entry) => entry.Kind switch
    {
        EntryKind.Frame => System.IO.Path.GetFileName(entry.Path ?? "?"),
        EntryKind.SessionStart => "session start",
        _ => "session end",
    };

    private void Publish(UploaderState state, string? message, UploadProblem problem = UploadProblem.None)
    {
        var view = _view;
        var waiting = view is { } v ? v.Pending.Count + (v.Destination?.Count ?? 0) : 0;
        _status = _status with
        {
            State = state,
            Waiting = waiting,
            Message = message,
            SessionId = view?.Destination?.OpenSessionId(),
            Problem = problem,
            Elsewhere = view is { } w ? _store.CountExcept(w.Pending, w.Destination) : 0,
        };
        StatusChanged?.Invoke();
    }
}
