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
    /// <summary>The entry's file name without ".json"; unique across processes, sorts in capture order.</summary>
    public string Id { get; init; } = "";
    public EntryKind Kind { get; init; }
    public string? Path { get; init; }
    public string? Target { get; init; }
    public string? Sequence { get; init; }
    public DateTimeOffset Created { get; init; }

    /// <summary>
    /// The sequence this entry belongs to: an id the plugin makes when a NINA
    /// sequence starts (32 hex digits). Start, frames and end carry it, and the
    /// server files them by it, so however late one of them is sent it reaches
    /// its own night. Null only for an end with nothing to end.
    /// </summary>
    public string? Session { get; init; }
}

/// <summary>
/// One owner's queue between NINA and the network, kept on disk.
///
/// An owner is a NINA profile together with the Bortle server and plugin key
/// that were active when the entries were made (see <see cref="QueueStore"/>):
/// what was queued for one account is only ever sent to that account.
///
/// One small JSON file per entry, written to a temporary name and moved into
/// place without overwriting, so a crash leaves either the whole entry or none
/// of it, the queue survives NINA restarting, and two NINA instances writing to
/// the same queue never replace or delete each other's entries: every name
/// carries the writing queue object's tag.
///
/// Sending is guarded across processes: only the instance holding the drain
/// lock (<see cref="TryOwn"/>) sends from a queue.
/// </summary>
public sealed class FrameQueue : IDisposable
{
    /// <summary>
    /// Tells this queue object's entries apart from another's: each NINA
    /// process opens its own FrameQueue for a directory (QueueStore keeps one
    /// per directory), so two processes never share a tag.
    /// </summary>
    private readonly string Instance = Guid.NewGuid().ToString("N")[..12];

    // One counter and one clock floor for the process: entries of the same
    // tick are ordered by the counter, and an id never sorts before one this
    // process (or, from the disk, an earlier run) has already handed out, even
    // when the PC's clock is stepped back.
    private static long s_counter;
    private static long s_lastTicks;
    private static readonly object s_order = new();

    private readonly string _dir;
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
        _lockFile = System.IO.Path.Combine(directory, "drain.lock");
        Directory.CreateDirectory(_dir);
        foreach (var file in Files())
        {
            // Continue after the newest entry already waiting: a clock that
            // was ahead when it was written must not put tonight before it.
            if (long.TryParse(System.IO.Path.GetFileName(file).Split('-')[0], out var ticks))
            {
                lock (s_order) s_lastTicks = Math.Max(s_lastTicks, ticks);
            }
        }
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

    public string Root { get; }

    /// <summary>The folder name, which is the owner's (see <see cref="QueueStore"/>).</summary>
    public string Name => System.IO.Path.GetFileName(Root);

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

    /// <summary>The server has taken something sent with this queue's key and address.</summary>
    public bool Accepted => File.Exists(System.IO.Path.Combine(Root, "accepted"));

    /// <summary>
    /// Where entries of this queue go instead, after its key or address was
    /// refused before it ever worked: the profile's pending queue (its name),
    /// or null.
    /// </summary>
    public string? RejectedInto
    {
        get
        {
            try
            {
                var file = System.IO.Path.Combine(Root, "rejected");
                return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The key and address work: noted once, and any rejection undone.</summary>
    public void MarkAccepted()
    {
        try
        {
            Directory.CreateDirectory(Root);
            var file = System.IO.Path.Combine(Root, "accepted");
            if (!File.Exists(file)) File.WriteAllText(file, DateTimeOffset.UtcNow.ToString("o"));
            File.Delete(System.IO.Path.Combine(Root, "rejected"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A key or address that never worked: hand every entry to the profile's
    /// pending queue, which goes with whatever key the profile has next, and
    /// send new ones there too. Entries keep their ids, so the order stays.
    /// </summary>
    public int RejectInto(FrameQueue pending)
    {
        var moved = 0;
        lock (_lock)
        {
            try
            {
                File.WriteAllText(System.IO.Path.Combine(Root, "rejected"), pending.Name);
                Directory.CreateDirectory(pending._dir);
                foreach (var file in Files().ToList())
                {
                    try
                    {
                        File.Move(file, System.IO.Path.Combine(pending._dir, System.IO.Path.GetFileName(file)));
                        moved++;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // locked for a moment: the next refusal moves it
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        // no wake: nothing new to send, and the refused key waits its pause
        return moved;
    }

    /// <summary>Entries set aside as damaged.</summary>
    public int DamagedCount => Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, "*.bad").Count() : 0;

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
            // the folder may have been cleared by hand meanwhile
            Directory.CreateDirectory(_dir);
            while (true)
            {
                long ticks;
                lock (s_order)
                {
                    ticks = Math.Max(created.UtcTicks, s_lastTicks);
                    s_lastTicks = ticks;
                }
                var id = $"{ticks:D19}-{Interlocked.Increment(ref s_counter):D9}-{Instance}";
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

    /// <summary>Whether any entry of this sequence is still waiting.</summary>
    public bool HasSession(string session)
    {
        lock (_lock)
        {
            foreach (var file in Files())
            {
                try
                {
                    if (JsonSerializer.Deserialize<QueueEntry>(File.ReadAllText(file))?.Session == session) return true;
                }
                catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
                {
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Become the one process that sends from this queue. Held until
    /// <see cref="Release"/> or <see cref="Dispose"/>; false while another NINA holds it.
    /// </summary>
    public bool TryOwn()
    {
        lock (_lock)
        {
            if (_drainLock is not null) return true;
            try
            {
                Directory.CreateDirectory(Root);
                _drainLock = new FileStream(
                    _lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
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

    public void Dispose() => Release();

    private string FileOf(string id) => System.IO.Path.Combine(_dir, id + ".json");

    private IEnumerable<string> Files() =>
        Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, "*.json") : Enumerable.Empty<string>();

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
/// The one exception is a profile that had no complete key when it captured:
/// those entries wait in the profile's "pending" queue and go with the key that
/// same profile has when they are sent — the owner is the profile, and a key
/// arriving a moment late was the normal case (2026-11-12).
/// </summary>
public sealed class QueueStore : IDisposable
{
    private readonly string _root;
    private readonly string? _legacy;
    private readonly Dictionary<string, FrameQueue> _queues = new();
    private readonly object _lock = new();
    private (string Id, string Queue)? _running;
    private FileStream? _runningMarker;

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

    public FrameQueue Pending(string profileId) => For("p-" + Hash(profileId));

    /// <summary>A queue by its folder name (what <see cref="FrameQueue.Name"/> gave).</summary>
    public FrameQueue ByName(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[pd]-[0-9a-f]{20}$"))
        {
            throw new ArgumentException($"not a queue name: {name}");
        }
        var queue = For(name);
        return queue.RejectedInto is { } into && into != name && into.StartsWith("p-") ? ByName(into) : queue;
    }

    /// <summary>
    /// The sequence this NINA is running, kept on disk: if NINA stops without
    /// saying the sequence finished, the next start of the plugin ends it.
    /// Each process has a marker of its own, held open while it runs, so a
    /// second NINA on the machine never takes the first one's sequence for
    /// an orphan (review round 5).
    /// </summary>
    public (string Id, string Queue)? Running
    {
        get
        {
            lock (_lock) return _running;
        }
        set
        {
            lock (_lock)
            {
                _running = value;
                try
                {
                    if (value is { } v)
                    {
                        if (_runningMarker is null)
                        {
                            Directory.CreateDirectory(RunningDir);
                            _runningMarker = new FileStream(
                                System.IO.Path.Combine(RunningDir, Guid.NewGuid().ToString("N") + ".txt"),
                                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                        }
                        var bytes = System.Text.Encoding.UTF8.GetBytes($"{v.Id} {v.Queue}");
                        _runningMarker.SetLength(0);
                        _runningMarker.Write(bytes, 0, bytes.Length);
                        _runningMarker.Flush(true);
                    }
                    else if (_runningMarker is { } marker)
                    {
                        _runningMarker = null;
                        var file = marker.Name;
                        marker.Dispose();
                        File.Delete(file);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // best effort: at worst the server's idle sweep ends that night
                }
            }
        }
    }

    /// <summary>
    /// The sequences of runs that stopped without saying they finished: the
    /// markers no live process holds. Each is returned once, and removed.
    /// </summary>
    public List<(string Id, string Queue)> TakeOrphans()
    {
        var found = new List<(string Id, string Queue)>();
        string[] files;
        try
        {
            if (!Directory.Exists(RunningDir)) return found;
            files = Directory.GetFiles(RunningDir, "*.txt");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return found;
        }
        foreach (var file in files)
        {
            lock (_lock)
            {
                if (_runningMarker is not null && file == _runningMarker.Name) continue;
            }
            try
            {
                // opens only when the NINA that wrote it is gone; deleted on close,
                // so two plugins starting together cannot both take it
                using var stream = new FileStream(
                    file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                using var reader = new StreamReader(stream);
                var parts = reader.ReadToEnd().Trim().Split(' ');
                if (parts.Length == 2) found.Add((parts[0], parts[1]));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // held by a NINA that is still running
            }
        }
        return found;
    }

    private string RunningDir => System.IO.Path.Combine(_root, "running");

    public FrameQueue Destination(string profileId, ClientSettings settings) =>
        For("d-" + Hash(profileId, Normalize(settings.ServerUrl), settings.Key.Trim()));

    /// <summary>The queue new entries of this context go to.</summary>
    public FrameQueue For(CaptureContext context)
    {
        if (!context.Settings.IsComplete) return Pending(context.ProfileId);
        var destination = Destination(context.ProfileId, context.Settings);
        // a key or address that was refused before it ever worked owns nothing
        return destination.RejectedInto is null ? destination : Pending(context.ProfileId);
    }

    /// <summary>Everything waiting in any queue but these.</summary>
    public int CountExcept(params FrameQueue?[] these)
    {
        var skip = these.Where(q => q is not null).Select(q => System.IO.Path.GetFullPath(q!.Root)).ToHashSet();
        var total = 0;
        if (!Directory.Exists(_root)) return 0;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            if (skip.Contains(System.IO.Path.GetFullPath(dir))) continue;
            var queue = System.IO.Path.Combine(dir, "queue");
            if (Directory.Exists(queue)) total += Directory.EnumerateFiles(queue, "*.json").Count();
        }
        return total;
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

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var queue in _queues.Values) queue.Dispose();
            _queues.Clear();
            // the marker stays on disk: the next run ends that sequence
            _runningMarker?.Dispose();
            _runningMarker = null;
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
