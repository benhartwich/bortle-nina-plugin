using System.Security.Cryptography;
using System.Text;
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
    /// <summary>The entry's file name without ".json"; unique across processes.</summary>
    public string Id { get; init; } = "";
    public EntryKind Kind { get; init; }
    public string? Path { get; init; }
    public string? Target { get; init; }
    public string? Sequence { get; init; }
    public DateTimeOffset Created { get; init; }

    /// <summary>
    /// The logical session this entry belongs to: one per NINA sequence, made
    /// by the plugin when the sequence starts, so a start, its frames and its
    /// end always meet the same server session however late one of them is
    /// sent. Null only for an end with nothing to end.
    /// </summary>
    public string? Session { get; init; }
}

/// <summary>
/// A server session the plugin opened for one logical session, when it was
/// last used and, once ended, when.
/// </summary>
/// <remarks>
/// <see cref="LastUsed"/> and <see cref="Ended"/> are CAPTURE times (when NINA
/// made the entry), so a night sent late is judged as it happened.
/// <see cref="Accepted"/>: the server has taken a frame into it.
/// <see cref="Replaced"/>: opened because the server did not know the one before.
/// </remarks>
public sealed record SessionState(
    string Id,
    DateTimeOffset LastUsed,
    DateTimeOffset? Ended = null,
    bool Accepted = false,
    bool Replaced = false);

/// <summary>
/// One owner's queue between NINA and the network, kept on disk.
///
/// An owner is a NINA profile together with the Bortle server and plugin key
/// that were active when the entries were made (see <see cref="QueueStore"/>):
/// what was queued for one account is only ever sent to that account.
///
/// Session starts, frames and session ends go through the same queue, so the
/// server hears them in the order they happened. One small JSON file per entry,
/// written to a temporary name and moved into place without overwriting, so a
/// crash leaves either the whole entry or none of it, the queue survives NINA
/// restarting, and two NINA instances writing to the same queue never replace
/// or delete each other's entries: every name carries the writing process's tag.
///
/// Sending is guarded across processes: only the instance holding the drain
/// lock (<see cref="TryOwn"/>) sends from a queue and keeps its session map.
/// </summary>
public sealed class FrameQueue : IDisposable
{
    /// <summary>
    /// Tells this queue object's entries apart from another's: each NINA
    /// process opens its own FrameQueue for a directory (QueueStore keeps one
    /// per directory), so two processes never share a tag or a counter.
    /// </summary>
    private readonly string Instance = Guid.NewGuid().ToString("N")[..12];

    // One counter for the process: it orders entries of the same tick across
    // this process's queues (a profile's pending queue and its destination).
    private static long s_counter;

    private readonly string _dir;
    private readonly string _sessionsFile;
    private readonly string _lockFile;
    private readonly object _lock = new();
    private FileStream? _drainLock;

    public event Action? Changed;

    /// <summary>A damaged entry was set aside (its file name, now with ".bad").</summary>
    public event Action<string>? Damaged;

    public FrameQueue(string directory)
    {
        Root = directory;
        _dir = System.IO.Path.Combine(directory, "queue");
        _sessionsFile = System.IO.Path.Combine(directory, "sessions.json");
        _lockFile = System.IO.Path.Combine(directory, "drain.lock");
        Directory.CreateDirectory(_dir);
        // Temporary files a crash left behind; an entry is never a .tmp.
        foreach (var tmp in Directory.EnumerateFiles(_dir, "*.tmp"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tmp) > TimeSpan.FromDays(1)) File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Entries set aside as damaged.</summary>
    public int DamagedCount => Directory.EnumerateFiles(_dir, "*.bad").Count();

    public string Root { get; }

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

    public QueueEntry Enqueue(
        EntryKind kind,
        DateTimeOffset created,
        string? session,
        string? path = null,
        string? target = null,
        string? sequence = null)
    {
        QueueEntry entry;
        lock (_lock)
        {
            while (true)
            {
                // Time first, so the names sort in the order things happened;
                // the counter orders entries made in the same tick, the tag
                // keeps two processes from ever choosing the same name.
                var id = $"{created.UtcTicks:D19}-{Interlocked.Increment(ref s_counter):D9}-{Instance}";
                entry = new QueueEntry
                {
                    Id = id,
                    Kind = kind,
                    Path = path,
                    Target = string.IsNullOrWhiteSpace(target) ? null : target.Trim(),
                    Sequence = sequence,
                    Created = created,
                    Session = session,
                };
                var final = FileOf(id);
                var tmp = final + "." + Instance + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(entry));
                try
                {
                    File.Move(tmp, final, overwrite: false);
                    break;
                }
                catch (IOException) when (File.Exists(final))
                {
                    File.Delete(tmp); // never replace an entry; take the next name
                }
            }
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>
    /// The oldest entry, or null when there is nothing to send. An entry that
    /// cannot be READ just now (locked by a scanner or a backup) throws, so
    /// the caller retries; only an entry whose CONTENT is damaged is set aside.
    /// </summary>
    public QueueEntry? Peek()
    {
        string? damaged = null;
        QueueEntry? found = null;
        lock (_lock)
        {
            foreach (var file in Files().OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);
                }
                catch (FileNotFoundException)
                {
                    continue; // removed meanwhile by its owner
                }
                try
                {
                    var entry = JsonSerializer.Deserialize<QueueEntry>(text) ?? throw new JsonException("empty entry");
                    // the file name is the entry's identity, whatever is inside
                    found = entry with { Id = System.IO.Path.GetFileNameWithoutExtension(file) };
                    break;
                }
                catch (JsonException)
                {
                    // A damaged entry would block every frame behind it.
                    TryMove(file, file + ".bad");
                    damaged = System.IO.Path.GetFileName(file);
                }
            }
        }
        if (damaged is not null) Damaged?.Invoke(damaged);
        return found;
    }

    /// <summary>Remove exactly this entry, never another with a similar name.</summary>
    public void Remove(QueueEntry entry)
    {
        lock (_lock)
        {
            File.Delete(FileOf(entry.Id));
        }
        Changed?.Invoke();
    }

    /// <summary>Whether this entry is still waiting.</summary>
    public bool Contains(QueueEntry entry)
    {
        lock (_lock)
        {
            return File.Exists(FileOf(entry.Id));
        }
    }

    /// <summary>
    /// Become the one process that sends from this queue. Held until
    /// <see cref="Dispose"/>; false while another NINA holds it.
    /// </summary>
    public bool TryOwn()
    {
        lock (_lock)
        {
            if (_drainLock is not null) return true;
            try
            {
                _drainLock = new FileStream(
                    _lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Let another process send this queue.</summary>
    public void Release()
    {
        lock (_lock)
        {
            _drainLock?.Dispose();
            _drainLock = null;
        }
    }

    public SessionState? GetSession(string logical)
    {
        lock (_lock)
        {
            return LoadSessions().TryGetValue(logical, out var state) ? state : null;
        }
    }

    /// <summary>Remember (or, with null, forget) the server session of a logical session.</summary>
    public void PutSession(string logical, SessionState? state, DateTimeOffset now)
    {
        lock (_lock)
        {
            var all = LoadSessions();
            if (state is null) all.Remove(logical);
            else all[logical] = state;
            // forget what no entry can still refer to
            foreach (var key in all.Where(kv => now - kv.Value.LastUsed > TimeSpan.FromDays(3)).Select(kv => kv.Key).ToList())
            {
                all.Remove(key);
            }
            var tmp = _sessionsFile + "." + Instance + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            File.Move(tmp, _sessionsFile, overwrite: true);
        }
    }

    /// <summary>The server session used last that has not ended, for the status line.</summary>
    public string? OpenSessionId()
    {
        lock (_lock)
        {
            return LoadSessions().Values
                .Where(s => s.Ended is null)
                .OrderByDescending(s => s.LastUsed)
                .Select(s => s.Id)
                .FirstOrDefault();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _drainLock?.Dispose();
            _drainLock = null;
        }
    }

    private Dictionary<string, SessionState> LoadSessions()
    {
        try
        {
            return File.Exists(_sessionsFile)
                ? JsonSerializer.Deserialize<Dictionary<string, SessionState>>(File.ReadAllText(_sessionsFile)) ?? new()
                : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new();
        }
    }

    private string FileOf(string id) => System.IO.Path.Combine(_dir, id + ".json");

    private IEnumerable<string> Files() => Directory.EnumerateFiles(_dir, "*.json");

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Who captured: the active NINA profile, its Bortle settings, and whether it uploads.</summary>
public sealed record CaptureContext(string ProfileId, ClientSettings Settings, bool UploadEnabled = true);

/// <summary>
/// All queues, one directory per owner under one root.
///
/// The owner of an entry is fixed when it is queued: the NINA profile plus the
/// server address and plugin key that profile had at that moment (a hash of
/// them, never the key itself). A queue is sent only while that exact
/// combination is active again, so a backlog never moves to another account or
/// server when the settings or the profile change; it waits, counted in the
/// status line, until its own profile and key are back.
///
/// The one exception is a profile that had no key yet when it captured: those
/// entries wait in the profile's "pending" queue and go to the first server and
/// key that same profile is given — the owner is the profile, and the key
/// arriving a moment late was the normal case (2026-11-12).
/// </summary>
public sealed class QueueStore : IDisposable
{
    private readonly string _root;
    private readonly string? _legacy;
    private readonly Dictionary<string, FrameQueue> _queues = new();
    private readonly object _lock = new();

    public event Action? Changed;

    public event Action<string>? Damaged;

    /// <param name="legacyQueue">
    /// The single queue of 0.1.0.x, whose entries record no owner: never sent,
    /// only counted so the status line can say they are there.
    /// </param>
    public QueueStore(string root, string? legacyQueue = null)
    {
        _root = root;
        _legacy = legacyQueue;
        Directory.CreateDirectory(root);
    }

    public int LegacyCount
    {
        get
        {
            try
            {
                return _legacy is not null && Directory.Exists(_legacy)
                    ? Directory.EnumerateFiles(_legacy, "*.json").Count()
                    : 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    /// <summary>Let go of the drain lock of every queue but these.</summary>
    public void ReleaseExcept(params FrameQueue[] keep)
    {
        lock (_lock)
        {
            foreach (var queue in _queues.Values)
            {
                if (Array.IndexOf(keep, queue) < 0) queue.Release();
            }
        }
    }

    public FrameQueue Pending(string profileId) => For("p-" + Hash(profileId));

    public FrameQueue Destination(string profileId, ClientSettings settings) =>
        For("d-" + Hash(profileId, Normalize(settings.ServerUrl), settings.Key.Trim()));

    /// <summary>The queue new entries of this context go to.</summary>
    public FrameQueue For(CaptureContext context) =>
        context.Settings.IsComplete
            ? Destination(context.ProfileId, context.Settings)
            : Pending(context.ProfileId);

    /// <summary>Everything waiting in any queue but these.</summary>
    public int CountExcept(params FrameQueue?[] these)
    {
        var skip = these.Where(q => q is not null).Select(q => System.IO.Path.GetFullPath(q!.Root)).ToHashSet();
        var total = 0;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            if (skip.Contains(System.IO.Path.GetFullPath(dir))) continue;
            var queue = System.IO.Path.Combine(dir, "queue");
            if (Directory.Exists(queue)) total += Directory.EnumerateFiles(queue, "*.json").Count();
        }
        return total;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var queue in _queues.Values) queue.Dispose();
            _queues.Clear();
        }
    }

    private FrameQueue For(string name)
    {
        lock (_lock)
        {
            if (!_queues.TryGetValue(name, out var queue))
            {
                queue = new FrameQueue(System.IO.Path.Combine(_root, name));
                queue.Changed += () => Changed?.Invoke();
                queue.Damaged += file => Damaged?.Invoke(file);
                _queues[name] = queue;
            }
            return queue;
        }
    }

    private static string Normalize(string url) => (url ?? "").Trim().TrimEnd('/').ToLowerInvariant();

    private static string Hash(params string[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }
}
