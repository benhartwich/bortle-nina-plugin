using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Bortle.Capture;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;

namespace Bortle.NinaPlugin;

/// <summary>
/// "Bortle Livestream": a small panel for NINA's imaging tab with one button.
/// The stream itself is made on the Bortle server from the frames this plugin
/// uploads anyway, so this panel sends no video and touches no equipment; it
/// only switches the server's stream on and off and says what it shows.
/// </summary>
[Export(typeof(NINA.Equipment.Interfaces.ViewModel.IDockableVM))]
public sealed class LivestreamPanel : DockableVM
{
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);

    private string _state = "Noch nicht abgefragt";
    private string _detail = "";
    private bool _isLive;
    private bool _busy;
    private CancellationTokenSource? _poll;

    [ImportingConstructor]
    public LivestreamPanel(IProfileService profileService) : base(profileService)
    {
        Title = "Bortle Livestream";
        // The compiled dictionary itself, not a pack URI: nothing to resolve.
        var templates = new LivestreamPanelTemplate();
        ImageGeometry = (GeometryGroup)templates["Bortle_LivestreamIcon"];
        ImageGeometry.Freeze();
        ToggleCommand = new AsyncCommand(ToggleAsync);
        RefreshCommand = new AsyncCommand(RefreshAsync);
        StartPolling();
    }

    public override bool IsTool { get; } = true;

    public string State
    {
        get => _state;
        private set { _state = value; RaisePropertyChanged(); }
    }

    public string Detail
    {
        get => _detail;
        private set { _detail = value; RaisePropertyChanged(); }
    }

    public bool IsLive
    {
        get => _isLive;
        private set
        {
            _isLive = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ButtonText));
        }
    }

    public bool IsIdle
    {
        get => !_busy;
    }

    public string ButtonText => IsLive ? "Beenden" : "Live gehen";

    public ICommand ToggleCommand { get; }

    public ICommand RefreshCommand { get; }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RaisePropertyChanged(nameof(IsIdle));
    }

    private async Task ToggleAsync()
    {
        var client = BortlePlugin.Client;
        if (client is null) return;
        SetBusy(true);
        try
        {
            var result = IsLive
                ? await client.StopLivestreamAsync(CancellationToken.None)
                : await client.StartLivestreamAsync(CancellationToken.None);
            Show(result);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshAsync()
    {
        var client = BortlePlugin.Client;
        if (client is null)
        {
            State = "Bortle-Plugin nicht geladen";
            return;
        }
        Show(await client.LivestreamAsync(CancellationToken.None));
    }

    private void Show(ApiResult<LivestreamStatus> result)
    {
        if (result.Outcome != Outcome.Ok || result.Value is null)
        {
            State = result.Outcome == Outcome.KeyMissing
                ? "Erst Server und Plugin-Schlüssel eintragen (Optionen → Plugins → Bortle)"
                : Explain(result.Message);
            Detail = "";
            return;
        }
        var s = result.Value;
        IsLive = s.IsLive;
        if (!s.Allowed)
        {
            State = "Livestreams gibt es vorerst nur für den Betreiber";
            Detail = "";
            return;
        }
        if (!s.Configured)
        {
            State = "Noch kein Stream-Schlüssel";
            Detail = "Einmal im Bortle-Cockpit eintragen: Profil → Livestream.";
            return;
        }
        State = s.State switch
        {
            "live" => "● Live",
            "ending" => "● Sequenz zu Ende – der Stream endet gleich",
            _ => "Nicht live",
        };
        Detail = s.IsLive && s.Showing is { } show && show.Frames > 0
            ? $"{show.Target} · {show.Frames} Bilder · {Hours(show.IntegrationSeconds)}"
            : s.IsLive
                ? "Wartet auf das erste Bild"
                : s.Reason ?? "";
    }

    /// <summary>"HTTP 409: Noch kein …" → "Noch kein …".</summary>
    private static string Explain(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "Keine Verbindung zu Bortle";
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        return message.StartsWith("HTTP ", StringComparison.Ordinal) && colon > 0 ? message[(colon + 2)..] : message;
    }

    private static string Hours(double seconds)
    {
        var minutes = (int)Math.Round(seconds / 60.0);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min";
    }

    private void StartPolling()
    {
        _poll = new CancellationTokenSource();
        var token = _poll.Token;
        _ = Task.Run(async () =>
        {
            // The plugin itself may still be starting; the first look waits a moment.
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (IsVisible) await RefreshAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Logger.Error($"Bortle: livestream status failed: {e.Message}");
                }
                await Task.Delay(Refresh, token).ConfigureAwait(false);
            }
        }, token);
    }

    private sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _run;
        public AsyncCommand(Func<Task> run) => _run = run;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;

        public async void Execute(object? parameter)
        {
            try
            {
                await _run();
            }
            catch (Exception e)
            {
                Logger.Error($"Bortle: livestream command failed: {e}");
            }
        }
    }
}
