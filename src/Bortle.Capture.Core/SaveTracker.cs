namespace Bortle.Capture;

/// <summary>
/// What NINA still has on its way to disk.
///
/// NINA's Take Exposure finishes when the exposure is downloaded; converting,
/// preparing and saving the image happen afterwards in the background
/// (TakeExposure.ProcessImageData → IImageSaveMediator.Enqueue → the
/// ImageSaveController's worker, which raises BeforeImageSaved when it picks
/// an image up and ImageSaved once the file is written). So the instruction
/// after the last exposure runs while that image is not saved yet: on
/// 2026-11-12 "Finish Bortle session" ended the session at 20:42:51 and the
/// 5 s dark it was waiting for was written at 20:43:04.
///
/// The tracker counts images between BeforeImageSaved and ImageSaved, and
/// remembers the last sign of imaging activity (ImagePrepared included, which
/// fires before the image even reaches the save queue). Everything is settled
/// once nothing is in flight and nothing happened for a quiet period — counted
/// from the later of the last activity and the moment someone started waiting,
/// because the image just exposed may not have shown up at all yet.
/// </summary>
public sealed class SaveTracker
{
    /// <summary>
    /// How long nothing may happen before the last image counts as saved. The
    /// gap it has to cover is the conversion between the end of the download
    /// and the save queue picking the image up: about a second for a 26 MP
    /// frame, more on a slow PC.
    /// </summary>
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromSeconds(20);

    /// <summary>
    /// An image whose save never reported back is forgotten after this long.
    /// NINA gives up writing a file after 5 minutes (ImageSaveController's
    /// write timeout), and a failed save raises no ImageSaved at all.
    /// </summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(6);

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly List<DateTimeOffset> _inFlight = new();
    private DateTimeOffset _lastActivity = DateTimeOffset.MinValue;

    public SaveTracker(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>An image was prepared: one may be on its way to the save queue.</summary>
    public void Activity()
    {
        lock (_lock)
        {
            _lastActivity = _clock();
        }
    }

    /// <summary>NINA picked an image up to save it (BeforeImageSaved).</summary>
    public void SaveStarted()
    {
        lock (_lock)
        {
            var now = _clock();
            _inFlight.Add(now);
            _lastActivity = now;
        }
    }

    /// <summary>NINA wrote an image (ImageSaved) — call after the frame is queued.</summary>
    public void SaveFinished()
    {
        lock (_lock)
        {
            if (_inFlight.Count > 0)
            {
                _inFlight.RemoveAt(0);
            }
            _lastActivity = _clock();
        }
    }

    public int InFlight
    {
        get
        {
            lock (_lock)
            {
                Expire(_clock());
                return _inFlight.Count;
            }
        }
    }

    /// <summary>
    /// Whether nothing is on its way to disk: no image in flight, and no
    /// activity for <paramref name="quiet"/> since the later of the last
    /// activity and <paramref name="waitingSince"/>.
    /// </summary>
    public bool Settled(DateTimeOffset waitingSince, TimeSpan quiet)
    {
        lock (_lock)
        {
            var now = _clock();
            Expire(now);
            var from = _lastActivity > waitingSince ? _lastActivity : waitingSince;
            return _inFlight.Count == 0 && now - from >= quiet;
        }
    }

    private void Expire(DateTimeOffset now) => _inFlight.RemoveAll(t => now - t > GiveUpAfter);
}
