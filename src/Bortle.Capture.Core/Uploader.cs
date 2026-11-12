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

public sealed record UploaderStatus(
    UploaderState State,
    int Waiting,
    string? Message,
    DateTimeOffset? LastSent,
    string? SessionId);

/// <summary>
/// Sends the queue to Bortle, one entry after the other, in a background task.
///
/// It never blocks NINA: the event handlers only enqueue. A failure leaves the
/// entry at the head and waits — 10 s, doubling up to 5 min — so a night with
/// the server down loses nothing and the frames follow when it is back. A
/// refusal a person has to fix (wrong key, quota) pauses until the settings
/// change. Frames the server will never take are skipped and logged.
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

    private readonly FrameQueue _queue;
    private readonly BortleClient _client;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SaveTracker? _saves;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private TimeSpan _retry = FirstRetry;
    private UploaderStatus _status;

    public event Action? StatusChanged;

    public Uploader(
        FrameQueue queue,
        BortleClient client,
        ILog log,
        Func<DateTimeOffset>? clock = null,
        SaveTracker? saves = null)
    {
        _queue = queue;
        _client = client;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _saves = saves;
        _status = new UploaderStatus(UploaderState.Idle, queue.Count, null, null, queue.LoadSession()?.Id);
        _queue.Changed += Wake;
    }

    public UploaderStatus Status => _status;

    /// <summary>How long NINA must be quiet before its last image counts as saved.</summary>
    internal TimeSpan SaveQuiet { get; set; } = SaveTracker.DefaultQuiet;

    internal TimeSpan Poll { get; set; } = TimeSpan.FromMilliseconds(500);

    public void Start() => _loop ??= Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Look at the queue now (something was added, or the settings changed).</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    public void SequenceStarted(string? sequence) => _queue.Enqueue(EntryKind.SessionStart, sequence: sequence);

    public void FrameSaved(string path, string? target) => _queue.Enqueue(EntryKind.Frame, path, target);

    public QueueEntry SequenceFinished() => _queue.Enqueue(EntryKind.SessionEnd);

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
        var settled = await WaitForSavesAsync(deadline, ct).ConfigureAwait(false);
        var end = SequenceFinished();
        while (_queue.HasPendingUpTo(end.Seq))
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
    /// </summary>
    public async Task EndAfterSavesAsync(TimeSpan timeout, CancellationToken ct)
    {
        await WaitForSavesAsync(_clock() + timeout, ct).ConfigureAwait(false);
        SequenceFinished();
    }

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
        _queue.Changed -= Wake;
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
    /// </summary>
    internal async Task<TimeSpan?> StepAsync(CancellationToken ct)
    {
        var entry = _queue.Peek();
        if (entry is null)
        {
            Publish(UploaderState.Idle, null);
            return TimeSpan.FromMinutes(1);
        }
        Publish(UploaderState.Sending, Describe(entry));
        var (outcome, message) = await HandleAsync(entry, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case Outcome.Ok:
                _queue.Remove(entry);
                _retry = FirstRetry;
                _status = _status with { LastSent = _clock() };
                Publish(UploaderState.Sending, null);
                return null;
            case Outcome.Drop:
                _log.Error($"Bortle upload: skipped {Describe(entry)}: {message}");
                _queue.Remove(entry);
                Publish(UploaderState.Sending, $"skipped {Describe(entry)}: {message}");
                return null;
            case Outcome.SessionGone:
                // The server forgot our session (or it was another install's):
                // start a fresh one and send the same entry again.
                _queue.SaveSession(null);
                return null;
            case Outcome.Pause:
                Publish(UploaderState.Paused, message);
                return PauseFor;
            default:
                Publish(UploaderState.Retrying, message);
                var wait = _retry;
                _retry = TimeSpan.FromTicks(Math.Min(_retry.Ticks * 2, MaxRetry.Ticks));
                return wait;
        }
    }

    private async Task<(Outcome, string?)> HandleAsync(QueueEntry entry, CancellationToken ct)
    {
        var session = _queue.LoadSession();
        switch (entry.Kind)
        {
            case EntryKind.SessionStart:
            {
                var started = await _client.StartSessionAsync(entry.Sequence, ct).ConfigureAwait(false);
                if (started.Outcome == Outcome.Ok && started.Value is { } info)
                {
                    _queue.SaveSession(new SessionState(info.Id, _clock()));
                    _queue.SaveEndedSession(null);
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
                if (session is null && _queue.LoadEndedSession() is { } ended)
                {
                    if (_clock() - ended.LastUsed <= LateFrameWindow)
                    {
                        // Saved after its session ended: it still belongs there.
                        var late = await _client.PutFrameAsync(ended.Id, entry.Path, entry.Target, ct)
                            .ConfigureAwait(false);
                        if (late.Outcome != Outcome.SessionGone)
                        {
                            if (late.Outcome == Outcome.Ok)
                            {
                                _log.Info($"Bortle: {Describe(entry)} joined session {ended.Id} after its end");
                            }
                            return (late.Outcome, late.Message);
                        }
                    }
                    _queue.SaveEndedSession(null); // too late, or the server no longer knows it
                }
                if (session is null || _clock() - session.LastUsed > SessionIdle)
                {
                    // A frame without a sequence start (the plugin was installed
                    // mid-night, or images were taken by hand), or last night's
                    // session never ended: ask for tonight's.
                    var started = await _client.StartSessionAsync(null, ct).ConfigureAwait(false);
                    if (started.Outcome != Outcome.Ok || started.Value is null)
                    {
                        return (started.Outcome == Outcome.SessionGone ? Outcome.Retry : started.Outcome,
                            started.Message);
                    }
                    session = new SessionState(started.Value.Id, _clock());
                    _queue.SaveSession(session);
                }
                var sent = await _client.PutFrameAsync(session.Id, entry.Path, entry.Target, ct).ConfigureAwait(false);
                if (sent.Outcome == Outcome.Ok)
                {
                    _queue.SaveSession(session with { LastUsed = _clock() });
                }
                return (sent.Outcome, sent.Message);
            }
            case EntryKind.SessionEnd:
            {
                if (session is null)
                {
                    return (Outcome.Ok, null); // ended already (the instruction, then NINA's own event)
                }
                var ended = await _client.EndSessionAsync(session.Id, ct).ConfigureAwait(false);
                if (ended.Outcome is Outcome.Ok or Outcome.SessionGone)
                {
                    _queue.SaveSession(null);
                    _queue.SaveEndedSession(
                        ended.Outcome == Outcome.Ok ? new SessionState(session.Id, _clock()) : null);
                    _log.Info($"Bortle session {session.Id} ended");
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

    private void Publish(UploaderState state, string? message)
    {
        _status = _status with
        {
            State = state,
            Waiting = _queue.Count,
            Message = message,
            SessionId = _queue.LoadSession()?.Id,
        };
        StatusChanged?.Invoke();
    }
}
