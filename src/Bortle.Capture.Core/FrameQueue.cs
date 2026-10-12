using System.Text.Json;

namespace Bortle.Capture;

public enum EntryKind
{
    SessionStart,
    Frame,
    SessionEnd,
}

/// <summary>One thing to tell Bortle, in the order NINA did it.</summary>
public sealed record QueueEntry
{
    public long Seq { get; init; }
    public EntryKind Kind { get; init; }
    public string? Path { get; init; }
    public string? Target { get; init; }
    public string? Sequence { get; init; }
    public DateTimeOffset Created { get; init; }
}

/// <summary>The session the server gave us, and when we last used it.</summary>
public sealed record SessionState(string Id, DateTimeOffset LastUsed);

/// <summary>
/// The queue between NINA and the network, kept on disk.
///
/// Session starts, frames and session ends go through the same queue, so the
/// server hears them in the order they happened: a session end arrives only
/// after every frame before it. One small JSON file per entry, written to a
/// temporary name and moved into place, so a crash or a power cut leaves either
/// the whole entry or none of it — and the queue survives NINA restarting.
/// </summary>
public sealed class FrameQueue
{
    private readonly string _dir;
    private readonly string _sessionFile;
    private readonly object _lock = new();
    private long _nextSeq;

    public event Action? Changed;

    public FrameQueue(string directory)
    {
        _dir = System.IO.Path.Combine(directory, "queue");
        _sessionFile = System.IO.Path.Combine(directory, "session.json");
        Directory.CreateDirectory(_dir);
        _nextSeq = Files().Select(SeqOf).DefaultIfEmpty(0).Max() + 1;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return Files().Count();
            }
        }
    }

    public QueueEntry Enqueue(EntryKind kind, string? path = null, string? target = null, string? sequence = null)
    {
        QueueEntry entry;
        lock (_lock)
        {
            entry = new QueueEntry
            {
                Seq = _nextSeq++,
                Kind = kind,
                Path = path,
                Target = string.IsNullOrWhiteSpace(target) ? null : target.Trim(),
                Sequence = sequence,
                Created = DateTimeOffset.UtcNow,
            };
            var final = System.IO.Path.Combine(_dir, $"{entry.Seq:D12}.json");
            var tmp = final + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entry));
            File.Move(tmp, final, overwrite: true);
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>The oldest entry, or null when there is nothing to send.</summary>
    public QueueEntry? Peek()
    {
        lock (_lock)
        {
            foreach (var file in Files().OrderBy(SeqOf))
            {
                try
                {
                    return JsonSerializer.Deserialize<QueueEntry>(File.ReadAllText(file));
                }
                catch (Exception e) when (e is JsonException or IOException)
                {
                    // A damaged entry would block every frame behind it.
                    File.Move(file, file + ".bad", overwrite: true);
                }
            }
            return null;
        }
    }

    public void Remove(QueueEntry entry)
    {
        lock (_lock)
        {
            File.Delete(System.IO.Path.Combine(_dir, $"{entry.Seq:D12}.json"));
        }
        Changed?.Invoke();
    }

    /// <summary>Whether an entry up to and including <paramref name="seq"/> is still waiting.</summary>
    public bool HasPendingUpTo(long seq)
    {
        lock (_lock)
        {
            return Files().Any(f => SeqOf(f) <= seq);
        }
    }

    public SessionState? LoadSession()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(_sessionFile)
                    ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(_sessionFile))
                    : null;
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                return null;
            }
        }
    }

    public void SaveSession(SessionState? state)
    {
        lock (_lock)
        {
            if (state is null)
            {
                File.Delete(_sessionFile);
                return;
            }
            var tmp = _sessionFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state));
            File.Move(tmp, _sessionFile, overwrite: true);
        }
    }

    private IEnumerable<string> Files() => Directory.EnumerateFiles(_dir, "*.json");

    private static long SeqOf(string file) =>
        long.TryParse(System.IO.Path.GetFileNameWithoutExtension(file), out var seq) ? seq : 0;
}
