namespace Bortle.Capture;

/// <summary>
/// The plugin's status line in NINA's options, in plain words. Lives here
/// rather than in the plugin so it can be tested without NINA.
/// </summary>
public static class StatusText
{
    public const string KeyMissing = "Schlüssel fehlt in diesem NINA-Profil";
    public const string KeyRejected = "Schlüssel abgewiesen (401) – im aktiven Profil prüfen";

    public static string Describe(UploaderStatus s)
    {
        var state = s.State switch
        {
            UploaderState.Idle => "Alles übertragen",
            UploaderState.Sending => "Überträgt",
            UploaderState.Retrying => "Neuer Versuch gleich",
            UploaderState.Paused => "Angehalten",
            _ => "Gestoppt",
        };
        var line = $"{state} · {s.Waiting} in der Warteschlange";
        if (s.LastSent is { } at) line += $" · zuletzt {at.ToLocalTime():HH:mm:ss}";
        var detail = s.Problem switch
        {
            // The settings are per NINA profile: say so, because the key being
            // "gone" after a profile switch is the usual cause.
            UploadProblem.KeyMissing => $"{KeyMissing} (Optionen → Plugins → Bortle). Neuer Versuch in 30 s.",
            UploadProblem.KeyRejected => $"{KeyRejected}. Neuer Versuch in 30 s.",
            UploadProblem.Quota => $"Speicher des Kontos voll: {s.Message}",
            _ => s.Message,
        };
        return string.IsNullOrEmpty(detail) ? line : $"{line}\n{detail}";
    }
}

/// <summary>
/// Warn once when the server refuses the key, not on every 30 s retry; warn
/// again only after something was sent in between. (Every retry publishes
/// "Sending" first, so that alone is no sign the key works.)
/// </summary>
public sealed class KeyWarning
{
    private bool _warned;
    private DateTimeOffset? _lastSent;

    /// <summary>True when this status should raise NINA's notification now.</summary>
    public bool ShouldWarn(UploaderStatus s)
    {
        if (s.LastSent != _lastSent)
        {
            _lastSent = s.LastSent;
            _warned = false;
        }
        if (s.Problem != UploadProblem.KeyRejected || _warned)
        {
            return false;
        }
        _warned = true;
        return true;
    }
}
