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
    int Elsewhere = 0,
    int Legacy = 0);

/// <summary>
/// Sends the queues to Bortle, one entry after the other, in a background task.
///
/// It never blocks NINA: the event handlers only enqueue. A failure leaves the
/// entry at the head and waits — 10 s, doubling up to 5 min — so a night with
/// the server down loses nothing and the frames follow when it is back. A
/// refusal a person has to fix (wrong key, quota) pauses until the settings
/// change or a connection test succeeds. Frames the server will never take
/// are skipped and logged.
///
/// Every decision about which server session an entry belongs to is made from
/// when it was CAPTURED (<see cref="QueueEntry.Created"/>), never from when it
/// happens to be sent: a night sent after an outage is the same night.
/// </summary>
public sealed class Uploader : IDisposable
{
    /// <summary>The server closes a session after this long without a frame;
    /// frames captured this far apart belong to different sessions.</summary>
    public static readonly TimeSpan SessionIdle = TimeSpan.FromHours(6);

    /// <summary>
    /// A frame captured this long after its session ended still goes to that
    /// session: NINA writes the last image of a sequence in the background,
    /// after the sequence (and "Finish Bortle session") moved on.
    /// </summary>
    public static readonly TimeSpan LateFrameWindow = TimeSpan.FromMinutes(30);

    /// <summary>A frame whose folder stays unreachable this long is given up.</summary>
    public static readonly TimeSpan UnreachableGiveUp = TimeSpan.FromDays(7);

    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PauseFor = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan KeyPause = TimeSpan.FromSeconds(30);

    /// <summary>How often a profile with uploads switched off looks again (a wake cuts it short).</summary>
    internal static readonly TimeSpan DisabledPoll = TimeSpan.FromMinutes(1);

    /// <summary>How often a held session end is looked at while NINA finishes saving.</summary>
    internal static readonly TimeSpan HeldPoll = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Quiet time after the last settings change before the uploader looks
    /// again. The option fields save on every keystroke; waking on each would
    /// send one request per typed character — every one a 401 with half a key.
    /// </summary>
    internal TimeSpan SettingsSettle { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long NINA must be quiet before its last image counts as saved.</summary>
    internal TimeSpan SaveQuiet { get; set; } = SaveTracker.DefaultQuiet;

    internal TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(500);

    private readonly QueueStore _store;
    private readonly BortleClient _client;
    private readonly Func<CaptureContext> _context;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SaveTracker? _saves;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sessions = new();
    private Timer? _settle;
    private bool _disposed;
    private Task? _loop;
    private TimeSpan _retry = FirstRetry;
    private UploaderStatus _status;
    private (FrameQueue Pending, FrameQueue? Destination)? _view;

    // The logical session of the sequence running now, the one that ended
    // last (a frame NINA saves just after the end still belongs to it), the
    // one for frames taken outside any sequence, and the images still on their
    // way to disk when the next sequence started (they are the previous one's).
    private string? _current;
    private (string Id, DateTimeOffset At)? _lastEnded;
    private (string Id, DateTimeOffset LastUsed)? _adhoc;
    private (string Id, int Left, DateTimeOffset Until)? _carry;

    // Session ends this process queued and still holds back until NINA has
    // saved the sequence's last images. Persisted like any entry, so closing
    // NINA meanwhile loses nothing: a new process does not hold them.
    private readonly HashSet<string> _held = new();

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
        _status = new UploaderStatus(UploaderState.Stopped, 0, null, null, null, Legacy: store.LegacyCount);
        _store.Changed += Wake;
        _store.Damaged += OnDamaged;
        if (_saves is not null) _saves.Finished += OnSaveFinished;
    }


    /// <summary>
    /// Each save that ends uses up one image the previous sequence still had
    /// on its way, whether it became a frame or not (a snapshot, a JPEG).
    /// </summary>
    private void OnSaveFinished()
    {
        lock (_sessions)
        {
            if (_carry is { } carry)
            {
                _carry = carry.Left > 1 ? (carry.Id, carry.Left - 1, carry.Until) : null;
            }
        }
    }

    public UploaderStatus Status => _status;

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
    /// The server address, key or upload switch changed (possibly mid-typing):
    /// look at the queue once the field has been quiet for <see cref="SettingsSettle"/>.
    /// </summary>
    public void SettingsChanged()
    {
        lock (_wake)
        {
            if (_disposed) return;
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
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // --- what NINA tells us ------------------------------------------------------

    /// <summary>
    /// A sequence starts. It always gets its logical session, so frames and
    /// the end find each other even when uploads are switched on mid-sequence;
    /// only the start itself is not queued when <paramref name="queue"/> is false.
    /// </summary>
    public void SequenceStarted(string? sequence, bool queue = true)
    {
        string session;
        lock (_sessions)
        {
            // Images NINA is still writing were taken by the sequence before.
            var previous = _current ?? _lastEnded?.Id;
            var inFlight = _saves?.InFlight ?? 0;
            _carry = previous is not null && inFlight > 0
                ? (previous, inFlight, _clock() + SaveTracker.GiveUpAfter)
                : null;
            if (_current is { } unfinished)
            {
                _lastEnded = (unfinished, _clock());
            }
            session = NewSession();
            _current = session;
        }
        if (queue)
        {
            Enqueue(EntryKind.SessionStart, session, sequence: sequence);
        }
    }

    public void FrameSaved(string path, string? target) =>
        Enqueue(EntryKind.Frame, SessionForFrame(), path, target);

    /// <summary>Queue the end of the sequence running now, at once (nothing running: a no-op entry).</summary>
    public (FrameQueue Queue, QueueEntry Entry) SequenceFinished() => Enqueue(EntryKind.SessionEnd, TakeCurrent());

    /// <summary>
    /// Report the end of the session once NINA has saved every image it still
    /// had on its way and every frame before the end has gone, and wait for
    /// that — at most <paramref name="timeout"/> for both together, and no
    /// longer than the uploader can actually send. True when the end reached
    /// the server. Otherwise the end stays queued and follows the frames later,
    /// and an image saved after it still joins the session; nothing is lost.
    /// </summary>
    public async Task<bool> FinishSessionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _clock() + timeout;
        var (queue, end) = await EndWhenSavedAsync(deadline, ct).ConfigureAwait(false);
        while (queue.Contains(end.Entry))
        {
            if (_clock() >= deadline)
            {
                return false;
            }
            if (_status.State == UploaderState.Paused)
            {
                // Switched off, no key, wrong key, quota or another NINA:
                // waiting would only hold the sequence. The end stays queued.
                return false;
            }
            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }
        return end.Settled;
    }

    /// <summary>
    /// NINA's own "sequence finished": the end is queued at once (so closing
    /// NINA now loses nothing) but held back until its last images are saved
    /// (at most <paramref name="timeout"/>), without holding NINA up. The
    /// session is fixed now, so a delayed end never closes the next one.
    /// </summary>
    public async Task EndAfterSavesAsync(TimeSpan timeout, CancellationToken ct)
    {
        await EndWhenSavedAsync(_clock() + timeout, ct).ConfigureAwait(false);
    }

    private async Task<(FrameQueue, (QueueEntry Entry, bool Settled))> EndWhenSavedAsync(
        DateTimeOffset deadline, CancellationToken ct)
    {
        var session = TakeCurrent();
        var queue = _store.For(_context());
        var held = queue.Enqueue(
            EntryKind.SessionEnd, _clock(), session, written: e => { lock (_held) _held.Add(e.Id); });
        try
        {
            var settled = await WaitForSavesAsync(deadline, ct).ConfigureAwait(false);
            // Behind the images saved meanwhile: queue it again at the tail,
            // then drop the held copy. A crash in between leaves two ends of
            // the same session; the second is a no-op.
            var end = queue.Enqueue(EntryKind.SessionEnd, _clock(), session);
            queue.Remove(held);
            return (queue, (end, settled));
        }
        finally
        {
            // whatever happened (stopped, a disk error), it must never stay
            // held: at worst it goes out where it stands
            lock (_held) _held.Remove(held.Id);
            Wake();
        }
    }

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
            if (_carry is { } carry && carry.Left > 0 && now <= carry.Until)
            {
                // an image the previous sequence took, written after this one
                // began (OnSaveFinished counts it off)
                return carry.Id;
            }
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

    // --- the loop ----------------------------------------------------------------

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
        lock (_wake)
        {
            if (_disposed) return;
            _disposed = true;
            _settle?.Dispose();
        }
        _store.Changed -= Wake;
        _store.Damaged -= OnDamaged;
        if (_saves is not null) _saves.Finished -= OnSaveFinished;
        _store.Dispose(); // releases this process's drain locks
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
                try
                {
                    _log.Error($"Bortle upload: {e}");
                    Publish(UploaderState.Retrying, e.Message);
                }
                catch (Exception)
                {
                    // not even the status could be written; still keep going
                }
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
    /// Handle the next entry once. Returns how long to wait before the next
    /// step, or null to carry straight on. A wake cuts any wait short.
    ///
    /// Only the active profile's queues are looked at, and of those only the
    /// one for the server and key it has now plus what it queued before it had
    /// a key, merged in the order things were captured. Everything else waits
    /// for its own owner. This process holds the drain lock of exactly the
    /// queues it is sending and lets go of every other.
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
            _store.ReleaseExcept();
            Publish(UploaderState.Paused, null, UploadProblem.Disabled);
            return DisabledPoll;
        }
        if (destination is null)
        {
            _store.ReleaseExcept();
            if (pending.Count == 0)
            {
                Publish(UploaderState.Idle, null);
                return TimeSpan.FromMinutes(1);
            }
            Publish(UploaderState.Paused, "Bortle server address or plugin key is missing", UploadProblem.KeyMissing);
            return KeyPause;
        }

        // Own the destination first; without it, hold nothing at all, so the
        // NINA that does own it can also take the pending queue.
        if (!destination.TryOwn())
        {
            _store.ReleaseExcept();
            Publish(UploaderState.Paused, "another N.I.N.A. is sending this queue", UploadProblem.Busy);
            return KeyPause;
        }
        if (pending.Count == 0 && pending.PinnedTo is not null)
        {
            pending.PinnedTo = null; // drained: the next key-less entries may go anywhere of this profile
        }
        // The pending queue only while it has something, only to the
        // destination it went to first, and left to another NINA holding it.
        var pin = pending.PinnedTo;
        var usePending = pending.Count > 0
            && (pin is null || pin == destination.Name)
            && pending.TryOwn();
        _store.ReleaseExcept(usePending ? new[] { destination, pending } : new[] { destination });

        FrameQueue queue;
        QueueEntry? entry;
        try
        {
            (queue, entry) = Next(destination, usePending ? pending : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an entry locked for a moment (a virus scanner, a backup): try again
            Publish(UploaderState.Retrying, e.Message);
            return Backoff();
        }
        if (entry is null)
        {
            Publish(UploaderState.Idle, null);
            return TimeSpan.FromMinutes(1);
        }
        bool held;
        lock (_held) held = _held.Contains(entry.Id);
        if (held)
        {
            // the end waits for NINA's last images; they will queue behind it
            Publish(UploaderState.Sending, "waiting for N.I.N.A. to finish saving");
            return HeldPoll;
        }
        Publish(UploaderState.Sending, Describe(entry));
        var (outcome, message) = await HandleAsync(entry, destination, _client.With(context.Settings), ct)
            .ConfigureAwait(false);
        switch (outcome)
        {
            case Outcome.Ok:
                if (queue == pending && pending.PinnedTo is null)
                {
                    pending.PinnedTo = destination.Name;
                }
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
                // for the same entry (HandleAsync bounds how often).
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
                return Backoff();
        }
    }

    private TimeSpan Backoff()
    {
        var wait = _retry;
        _retry = TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, MaxRetry.Ticks));
        return wait;
    }

    /// <summary>The earliest captured entry of the two queues.</summary>
    private static (FrameQueue, QueueEntry?) Next(FrameQueue destination, FrameQueue? pending)
    {
        var fromDestination = destination.Peek();
        var fromPending = pending?.Peek();
        if (fromPending is null) return (destination, fromDestination);
        if (fromDestination is null) return (pending!, fromPending);
        // entry ids begin with the capture time, then a counter
        return string.CompareOrdinal(fromPending.Id, fromDestination.Id) < 0
            ? (pending!, fromPending)
            : (destination, fromDestination);
    }

    private async Task<(Outcome, string?)> HandleAsync(
        QueueEntry entry, FrameQueue sessions, BortleClient client, CancellationToken ct)
    {
        var now = _clock();
        var captured = entry.Created;
        var logical = entry.Session;
        var state = logical is null ? null : sessions.GetSession(logical);
        switch (entry.Kind)
        {
            case EntryKind.SessionStart:
            {
                var started = await client.StartSessionAsync(entry.Sequence, ct).ConfigureAwait(false);
                if (started.Outcome == Outcome.Ok && started.Value is { } info && logical is not null)
                {
                    sessions.PutSession(logical, new SessionState(info.Id, captured), now);
                    _log.Info($"Bortle session {info.Id} (develop level {info.Level})");
                }
                return (started.Outcome, started.Message);
            }
            case EntryKind.Frame:
                return await SendFrameAsync(entry, logical ?? NewSession(), state, sessions, client, ct)
                    .ConfigureAwait(false);
            case EntryKind.SessionEnd:
            {
                if (logical is null || state is null || state.Ended is not null)
                {
                    // nothing running, never reached the server, or ended already
                    return (Outcome.Ok, null);
                }
                var ended = await client.EndSessionAsync(state.Id, ct).ConfigureAwait(false);
                if (ended.Outcome == Outcome.SessionGone && state.Misses == 0)
                {
                    // one 404 may be the proxy during a deploy: ask again before giving the session up
                    sessions.PutSession(logical, state with { Misses = 1 }, now);
                    return (Outcome.Retry, ended.Message);
                }
                if (ended.Outcome is Outcome.Ok or Outcome.SessionGone)
                {
                    sessions.PutSession(
                        logical, ended.Outcome == Outcome.Ok ? state with { Ended = captured } : null, now);
                    _log.Info($"Bortle session {state.Id} ended");
                    return (Outcome.Ok, null);
                }
                return (ended.Outcome, ended.Message);
            }
            default:
                return (Outcome.Drop, $"unknown entry {entry.Kind}");
        }
    }

    private async Task<(Outcome, string?)> SendFrameAsync(
        QueueEntry entry, string logical, SessionState? state, FrameQueue sessions, BortleClient client,
        CancellationToken ct)
    {
        var now = _clock();
        var captured = entry.Created;
        if (entry.Path is null)
        {
            return (Outcome.Drop, "no file");
        }
        if (!File.Exists(entry.Path))
        {
            // Gone for good only when its folder is there and it is not: a
            // network share that dropped out takes the folder with it.
            var folder = System.IO.Path.GetDirectoryName(entry.Path);
            if (folder is not null && Directory.Exists(folder))
            {
                return (Outcome.Drop, "the file is gone");
            }
            if (now - captured > UnreachableGiveUp)
            {
                return (Outcome.Drop, $"its folder has been unreachable for {UnreachableGiveUp.TotalDays:0} days");
            }
            return (Outcome.Retry, $"folder not reachable: {folder}");
        }

        if (state is { Ended: { } endedAt })
        {
            if (captured - endedAt <= LateFrameWindow)
            {
                // Taken just after its session ended: it still belongs there.
                var late = await client.PutFrameAsync(state.Id, entry.Path, entry.Target, ct).ConfigureAwait(false);
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
        else if (state is not null && captured - state.LastUsed > SessionIdle)
        {
            state = null; // a different night: the server has closed that one
        }

        // At most two server sessions per attempt: the one we have (or a new
        // one), and one replacement if the server says it does not know it. A
        // session refused at once although the server just made it means the
        // route is broken (a proxy, a rule), not the session: back off rather
        // than open a session per step.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (state is null)
            {
                var started = await client.StartSessionAsync(null, ct).ConfigureAwait(false);
                if (started.Outcome != Outcome.Ok || started.Value is null)
                {
                    return (started.Outcome == Outcome.SessionGone ? Outcome.Retry : started.Outcome, started.Message);
                }
                state = new SessionState(started.Value.Id, captured);
                sessions.PutSession(logical, state, now);
            }
            var sent = await client.PutFrameAsync(state.Id, entry.Path, entry.Target, ct).ConfigureAwait(false);
            if (sent.Outcome == Outcome.Ok)
            {
                var last = captured > state.LastUsed ? captured : state.LastUsed;
                sessions.PutSession(logical, state with { LastUsed = last, Accepted = true, Misses = 0 }, now);
                return (sent.Outcome, sent.Message);
            }
            if (sent.Outcome != Outcome.SessionGone)
            {
                return (sent.Outcome, sent.Message);
            }
            if (state.Accepted && state.Misses == 0)
            {
                // A session that took frames does not vanish for one 404: a
                // proxy during a deploy does that. Ask again after the backoff.
                sessions.PutSession(logical, state with { Misses = 1 }, now);
                return (Outcome.Retry, sent.Message);
            }
            if (!state.Accepted && state.Replaced)
            {
                // a replacement refused before it took a single frame
                return (Outcome.Retry, $"the server refuses frames even in a new session: {sent.Message}");
            }
            var replaced = !state.Accepted;
            state = null;
            sessions.PutSession(logical, null, now);
            if (replaced)
            {
                // the session we had never took a frame either; open one more, marked
                var started = await client.StartSessionAsync(null, ct).ConfigureAwait(false);
                if (started.Outcome != Outcome.Ok || started.Value is null)
                {
                    return (started.Outcome == Outcome.SessionGone ? Outcome.Retry : started.Outcome, started.Message);
                }
                state = new SessionState(started.Value.Id, captured, Replaced: true);
                sessions.PutSession(logical, state, now);
            }
        }
        return (Outcome.Retry, "the server refuses frames even in a new session");
    }

    private void OnDamaged(string file) =>
        _log.Error($"Bortle: a damaged queue entry was set aside as {file}.bad");

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
            Legacy = _store.LegacyCount,
        };
        StatusChanged?.Invoke();
    }
}
