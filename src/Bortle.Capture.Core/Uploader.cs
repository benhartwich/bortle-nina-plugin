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
    /// <summary>The server address redirects (e.g. http:// to https://).</summary>
    ServerAddress,
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
/// It keeps no server state. Every entry names its sequence (an id the plugin
/// makes when NINA starts one) and the server files it by that name, so a late
/// end can only end its own sequence, a frame sent days later still finds its
/// night, and a 404 never makes the plugin open anything.
/// </summary>
public sealed class Uploader : IDisposable
{
    /// <summary>Frames taken by hand this far apart are different nights.</summary>
    public static readonly TimeSpan SessionIdle = TimeSpan.FromHours(6);

    /// <summary>
    /// A frame saved this long after its sequence ended, without an exposure
    /// time to go by, still counts as that sequence's.
    /// </summary>
    public static readonly TimeSpan LateFrameWindow = TimeSpan.FromMinutes(30);

    /// <summary>A frame whose folder stays unreachable this long is given up.</summary>
    public static readonly TimeSpan UnreachableGiveUp = TimeSpan.FromDays(7);

    /// <summary>
    /// Slack when matching an exposure start against a sequence's start and end
    /// (both from this PC's clock; NINA stamps the exposure as it begins).
    /// </summary>
    internal static readonly TimeSpan Skew = TimeSpan.FromSeconds(15);

    internal static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PauseFor = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan KeyPause = TimeSpan.FromSeconds(30);

    /// <summary>How often a profile with uploads switched off looks again (a wake cuts it short).</summary>
    internal static readonly TimeSpan DisabledPoll = TimeSpan.FromMinutes(1);

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
    private readonly object _sequences = new();
    private Timer? _settle;
    private bool _disposed;
    private Task? _loop;
    private TimeSpan _retry = FirstRetry;
    private UploaderStatus _status;
    private (FrameQueue Pending, FrameQueue? Destination)? _view;
    private string? _lastSession;

    // The sequences of this run, newest last: when each started and ended.
    // A frame goes to the sequence its exposure began in.
    private readonly List<(string Id, DateTimeOffset Start, DateTimeOffset? End, string Queue)> _history = new();
    private (string Id, DateTimeOffset LastUsed)? _adhoc;

    // NINA's own sequence is still running (its SequenceFinished not seen yet),
    // even when "Finish Bortle session" already ended Bortle's sequence.
    private bool _ninaRunning;

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
        EndOrphan();
    }

    /// <summary>
    /// The sequence the last run was in when NINA stopped without saying it
    /// finished (a crash, a restart): end it now, so its night does not wait
    /// for the server's idle sweep.
    /// </summary>
    private void EndOrphan()
    {
        try
        {
            foreach (var orphan in _store.TakeOrphans())
            {
                _store.ByName(orphan.Queue).Enqueue(EntryKind.SessionEnd, _clock(), orphan.Id);
            }
        }
        catch (Exception e)
        {
            _log.Error($"Bortle: could not end the sequence of the last run: {e.Message}");
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
    /// A sequence starts. It always gets its id, so its frames and its end find
    /// each other even when uploads are switched on mid-sequence; only the
    /// start itself is not queued when <paramref name="queue"/> is false.
    /// </summary>
    public void SequenceStarted(string? sequence, bool queue = true)
    {
        lock (_sequences) _ninaRunning = true;
        // a sequence that never said it finished ends where the next begins
        if (TakeCurrent() is { } unfinished)
        {
            EndSequence(unfinished);
            Forget(unfinished);
        }
        Begin(sequence, queue);
    }

    /// <summary>A new sequence id: remembered (also on disk) and its start queued.</summary>
    private string Begin(string? sequence, bool queue, DateTimeOffset? exposed = null)
    {
        var session = NewSession();
        var target = _store.For(_context());
        // begun no later than the exposure that opened it: saving that frame
        // may have taken longer than the skew
        var start = exposed is { } t && t < _clock() ? t : _clock();
        lock (_sequences)
        {
            _history.Add((session, start, null, target.Name));
            if (_history.Count > 20) _history.RemoveAt(0);
        }
        _store.Running = (session, target.Name);
        if (queue)
        {
            target.Enqueue(EntryKind.SessionStart, _clock(), session, sequence: sequence);
        }
        return session;
    }

    /// <summary>
    /// NINA saved a frame. <paramref name="exposureStart"/> (NINA's own stamp)
    /// decides its sequence: an image NINA writes after the sequence ended, or
    /// after the next one began, still goes to the one it was taken in.
    /// </summary>
    public void FrameSaved(string path, string? target, DateTimeOffset? exposureStart = null)
    {
        var session = SequenceForFrame(exposureStart);
        if (session is null)
        {
            // "Finish Bortle session" stood mid-sequence and NINA carries on:
            // what follows is a sequence of its own, ended by NINA's finish
            session = Begin(null, queue: true, exposed: exposureStart);
        }
        Enqueue(EntryKind.Frame, session, path, target);
    }

    /// <summary>
    /// The running sequence finished: its end is queued at once (on disk, so
    /// closing NINA now loses nothing). Frames NINA still writes afterwards
    /// carry the same sequence id and the server files them with it.
    /// </summary>
    public (FrameQueue Queue, QueueEntry Entry) SequenceFinished()
    {
        lock (_sequences) _ninaRunning = false; // NINA's own finish
        var id = TakeCurrent();
        var ended = EndSequence(id);
        Forget(id);
        return ended;
    }

    /// <summary>
    /// "Finish Bortle session": queue the end now, then wait until NINA has
    /// saved its last images and everything of this sequence has reached
    /// Bortle — at most <paramref name="timeout"/>, and no longer than the
    /// uploader can actually send. True when it all arrived; otherwise it
    /// follows from the queue, nothing is lost.
    /// </summary>
    public async Task<bool> FinishSessionAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _clock() + timeout;
        // the instruction ends Bortle's sequence; NINA's may still go on
        var current = TakeCurrent();
        var (queue, end) = EndSequence(current);
        Forget(current);
        var settled = await WaitForSavesAsync(deadline, ct).ConfigureAwait(false);
        while (end.Session is { } session ? queue.HasSession(session) : queue.Contains(end))
        {
            if (_clock() >= deadline)
            {
                return false;
            }
            if (_status.State == UploaderState.Paused)
            {
                // Switched off, no key, wrong key, quota or another NINA:
                // waiting would only hold the sequence. It stays queued.
                return false;
            }
            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }
        return settled;
    }

    /// <summary>NINA's own "sequence finished": queue the end, at once.</summary>
    public Task EndAfterSavesAsync(TimeSpan timeout, CancellationToken ct)
    {
        SequenceFinished();
        return Task.CompletedTask;
    }

    private (FrameQueue, QueueEntry) Enqueue(
        EntryKind kind, string? session, string? path = null, string? target = null, string? sequence = null)
    {
        // The owner is fixed here, from the profile and settings active NOW.
        var queue = _store.For(_context());
        return (queue, queue.Enqueue(kind, _clock(), session, path, target, sequence));
    }

    /// <summary>
    /// Queue the end of a sequence where its start went: the account that
    /// opened it on the server, even when the profile or key changed since.
    /// </summary>
    private (FrameQueue, QueueEntry) EndSequence(string? session)
    {
        string? name = null;
        lock (_sequences)
        {
            foreach (var h in _history)
            {
                if (h.Id == session) name = h.Queue;
            }
        }
        var queue = name is null ? _store.For(_context()) : _store.ByName(name);
        return (queue, queue.Enqueue(EntryKind.SessionEnd, _clock(), session));
    }

    /// <summary>End the running sequence and return its id (null: nothing running).</summary>
    private string? TakeCurrent()
    {
        string id;
        lock (_sequences)
        {
            if (_history.Count == 0 || _history[^1].End is not null) return null;
            _history[^1] = _history[^1] with { End = _clock() };
            id = _history[^1].Id;
        }
        return id;
    }

    /// <summary>The marker goes only after the end is on disk: a crash in between ends it twice, never not at all.</summary>
    private void Forget(string? id)
    {
        if (id is not null && _store.Running is { } running && running.Id == id)
        {
            _store.Running = null;
        }
    }

    /// <summary>The sequence of a frame; null when NINA carries on after "Finish Bortle session".</summary>
    private string? SequenceForFrame(DateTimeOffset? exposureStart)
    {
        lock (_sequences)
        {
            var now = _clock();
            if (exposureStart is { } t)
            {
                // the newest sequence that was running when the exposure began
                for (var i = _history.Count - 1; i >= 0; i--)
                {
                    var (id, start, end, _) = _history[i];
                    if (start - Skew <= t && (end is null || t <= end + Skew)) return id;
                }
            }
            else if (_history.Count > 0)
            {
                var (id, _, end, _) = _history[^1];
                if (end is null || (now - end <= LateFrameWindow && !_ninaRunning)) return id;
            }
            if (_ninaRunning && _history.Count > 0 && _history[^1].End is not null)
            {
                return null; // taken after Bortle's end, in a NINA sequence that goes on
            }
            // A frame outside any sequence (taken by hand, or the plugin came
            // up mid-night): one id for such frames until they pause for as
            // long as the server keeps a night open.
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
                _log.Error($"Bortle: {_saves.InFlight} image(s) still being saved; finishing anyway");
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
    /// Only the active profile's queues are looked at: the one for the server
    /// and key it has now, plus what it queued before it had a key, merged in
    /// the order things were captured. Everything else waits for its own
    /// owner. This process holds the drain lock of exactly the queues it is
    /// sending and lets go of every other.
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
        // The destination first; without it hold nothing, so the NINA that
        // owns it can also take the pending queue.
        if (!destination.TryOwn())
        {
            _store.ReleaseExcept();
            Publish(UploaderState.Paused, "another N.I.N.A. is sending this queue", UploadProblem.Busy);
            return KeyPause;
        }
        // the pending queue only while it has something, and left to a NINA holding it
        var usePending = pending.Count > 0 && pending.TryOwn();
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
        Publish(UploaderState.Sending, Describe(entry));
        var (outcome, message) = await HandleAsync(entry, _client.With(context.Settings), ct).ConfigureAwait(false);
        switch (outcome)
        {
            case Outcome.Ok:
                queue.Remove(entry);
                if (!destination.Accepted) destination.MarkAccepted();
                _retry = FirstRetry;
                _status = _status with { LastSent = _clock() };
                Publish(UploaderState.Sending, null);
                return null;
            case Outcome.Drop:
                _log.Error($"Bortle upload: skipped {Describe(entry)}: {message}");
                queue.Remove(entry);
                Publish(UploaderState.Sending, $"skipped {Describe(entry)}: {message}");
                return null;
            case Outcome.KeyMissing:
                Publish(UploaderState.Paused, message, UploadProblem.KeyMissing);
                return KeyPause;
            case Outcome.KeyRejected:
                StrandNothing(destination, pending);
                Publish(UploaderState.Paused, message, UploadProblem.KeyRejected);
                return KeyPause;
            case Outcome.Redirected:
                StrandNothing(destination, pending);
                Publish(UploaderState.Paused, message, UploadProblem.ServerAddress);
                return KeyPause;
            case Outcome.Pause:
                Publish(UploaderState.Paused, message, UploadProblem.Quota);
                return PauseFor;
            default:
                Publish(UploaderState.Retrying, message);
                return Backoff();
        }
    }

    /// <summary>
    /// A key mistyped, or an address that is not the server's (http:// for
    /// https://), never worked for anyone: what was queued with it goes with
    /// the key and address the profile gets next, instead of waiting for
    /// settings that will never be right (final review before 0.1.2.0).
    /// </summary>
    private void StrandNothing(FrameQueue destination, FrameQueue pending)
    {
        if (destination.Accepted) return; // it worked before: a revoked key keeps its own
        var moved = destination.RejectInto(pending);
        if (moved > 0) _log.Info($"Bortle: {moved} entries wait for a working key or address");
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

    private async Task<(Outcome, string?)> HandleAsync(QueueEntry entry, BortleClient client, CancellationToken ct)
    {
        var session = entry.Session;
        switch (entry.Kind)
        {
            case EntryKind.SessionStart:
            {
                if (session is null) return (Outcome.Ok, null);
                var started = await client.StartSequenceAsync(session, entry.Sequence, ct).ConfigureAwait(false);
                if (started.Outcome == Outcome.Ok && started.Value is { } info)
                {
                    _lastSession = info.Id;
                    _log.Info($"Bortle session {info.Id} (develop level {info.Level})");
                }
                return (started.Outcome, started.Message);
            }
            case EntryKind.Frame:
            {
                if (entry.Path is null)
                {
                    return (Outcome.Drop, "no file");
                }
                if (!File.Exists(entry.Path))
                {
                    // Gone for good only when its folder is there and it is
                    // not: a network share that dropped out takes the folder.
                    var folder = System.IO.Path.GetDirectoryName(entry.Path);
                    if (folder is not null && Directory.Exists(folder))
                    {
                        return (Outcome.Drop, "the file is gone");
                    }
                    if (_clock() - entry.Created > UnreachableGiveUp)
                    {
                        return (Outcome.Drop, $"its folder has been unreachable for {UnreachableGiveUp.TotalDays:0} days");
                    }
                    return (Outcome.Retry, $"folder not reachable: {folder}");
                }
                var sent = await client.PutFrameAsync(session ?? NewSession(), entry.Path, entry.Target, ct)
                    .ConfigureAwait(false);
                if (sent.Outcome == Outcome.Ok && sent.Value?.Session is { } filedIn)
                {
                    _lastSession = filedIn;
                }
                return (sent.Outcome, sent.Message);
            }
            case EntryKind.SessionEnd:
            {
                if (session is null) return (Outcome.Ok, null); // nothing was running
                var ended = await client.EndSequenceAsync(session, ct).ConfigureAwait(false);
                if (ended.Outcome == Outcome.Ok)
                {
                    _log.Info($"Bortle: sequence {session} ended");
                }
                return (ended.Outcome, ended.Message);
            }
            default:
                return (Outcome.Drop, $"unknown entry {entry.Kind}");
        }
    }

    private void OnDamaged(string file) =>
        _log.Error($"Bortle: a damaged queue entry was set aside as {file}.bad");

    private static string Describe(QueueEntry entry) => entry.Kind switch
    {
        EntryKind.Frame => System.IO.Path.GetFileName(entry.Path ?? "?"),
        EntryKind.SessionStart => "sequence start",
        _ => "sequence end",
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
            SessionId = _lastSession,
            Problem = problem,
            Elsewhere = view is { } w ? _store.CountExcept(w.Pending, w.Destination) : 0,
            Legacy = _store.LegacyCount,
        };
        StatusChanged?.Invoke();
    }
}
