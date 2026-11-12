using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Bortle.Capture;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;

namespace Bortle.NinaPlugin;

/// <summary>
/// The Bortle plugin: hears NINA save frames and run sequences, and hands each
/// of those to the upload queue. It controls nothing — no equipment, no
/// sequence — and nothing it does can stop NINA from imaging: the handlers only
/// write a small file, and every network call happens in the background.
/// </summary>
[Export(typeof(IPluginManifest))]
public sealed class BortlePlugin : PluginBase, INotifyPropertyChanged
{
    /// <summary>For the sequence instruction, which NINA creates on its own.</summary>
    internal static Uploader? Uploader { get; private set; }

    private static readonly string[] FitsSuffixes = { ".fits", ".fit", ".fts" };

    private readonly IProfileService _profile;
    private readonly IImageSaveMediator _images;
    private readonly IImagingMediator _imaging;
    private readonly SaveTracker _saves = new();
    private readonly ISequenceMediator _sequence;
    private readonly IPluginOptionsAccessor _options;
    private readonly Uploader _uploader;
    private readonly BortleClient _client;
    private bool _warnedFormat;
    private readonly KeyWarning _keyWarning = new();
    private string _testResult = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    [ImportingConstructor]
    public BortlePlugin(
        IProfileService profile, IImageSaveMediator images, IImagingMediator imaging, ISequenceMediator sequence)
    {
        _profile = profile;
        _images = images;
        _imaging = imaging;
        _sequence = sequence;
        _options = new PluginOptionsAccessor(profile, Guid.Parse(Identifier));

        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "Bortle");
        // Long enough for a 120 MB frame on a slow uplink; a stalled upload is
        // retried, never waited on forever.
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _client = new BortleClient(http, () => new ClientSettings(ServerUrl, PluginKey));
        _uploader = new Uploader(new FrameQueue(dataDir), _client, new NinaLog(), saves: _saves);
        _uploader.StatusChanged += OnUploaderStatus;
        Uploader = _uploader;

        TestConnectionCommand = new RelayCommand(async () => await TestConnectionAsync());
        // What NINA still has on its way to disk (SaveTracker): an image is
        // prepared, picked up by the save worker, then written.
        _imaging.ImagePrepared += OnImagePrepared;
        _images.BeforeImageSaved += OnBeforeImageSaved;
        _images.ImageSaved += OnImageSaved;
        _profile.ProfileChanged += OnProfileChanged;
        _ = SubscribeSequenceAsync();
        _uploader.Start();
    }

    // --- settings (per NINA profile) ---------------------------------------------

    public string ServerUrl
    {
        get => _options.GetValueString(nameof(ServerUrl), "https://bortle.eu");
        set
        {
            _options.SetValueString(nameof(ServerUrl), (value ?? "").Trim());
            Raise();
            _uploader.Wake();
        }
    }

    public string PluginKey
    {
        get => _options.GetValueString(nameof(PluginKey), "");
        set
        {
            _options.SetValueString(nameof(PluginKey), (value ?? "").Trim());
            Raise();
            _uploader.Wake();
        }
    }

    public bool UploadEnabled
    {
        get => _options.GetValueBoolean(nameof(UploadEnabled), true);
        set
        {
            _options.SetValueBoolean(nameof(UploadEnabled), value);
            Raise();
        }
    }

    public string StatusText => Bortle.Capture.StatusText.Describe(_uploader.Status);

    public string TestResult
    {
        get => _testResult;
        private set
        {
            _testResult = value;
            Raise();
        }
    }

    public ICommand TestConnectionCommand { get; }

    private async Task TestConnectionAsync()
    {
        TestResult = "Prüfe …";
        try
        {
            var hello = await _client.HelloAsync(CancellationToken.None);
            if (hello.Outcome != Outcome.Ok || hello.Value is null)
            {
                TestResult = $"Keine Verbindung: {hello.Message}";
                return;
            }
            var plan = hello.Value.Plan?.Target is { } target
                ? $"Plan heute: {target}"
                : "Kein Plan für heute";
            var level = hello.Value.Plan?.DevelopLevel ?? hello.Value.Level?.Level ?? "auto";
            TestResult = $"Verbunden als {hello.Value.Owner} · {plan} · Entwicklung: {level}";
            _uploader.ConnectionConfirmed();
        }
        catch (Exception e)
        {
            TestResult = $"Keine Verbindung: {e.Message}";
        }
    }

    private void OnUploaderStatus()
    {
        Raise(nameof(StatusText));
        if (_keyWarning.ShouldWarn(_uploader.Status))
        {
            Notification.ShowWarning(
                "Bortle: " + Bortle.Capture.StatusText.KeyRejected
                + ". Die Plugin-Einstellungen gelten je NINA-Profil (Optionen → Plugins → Bortle).");
        }
    }

    // --- NINA's events -----------------------------------------------------------

    private void OnImagePrepared(object? sender, ImagePreparedEventArgs e) => _saves.Activity();

    private Task OnBeforeImageSaved(object sender, BeforeImageSavedEventArgs e)
    {
        _saves.SaveStarted();
        return Task.CompletedTask;
    }

    private void OnImageSaved(object? sender, ImageSavedEventArgs e)
    {
        try
        {
            if (!UploadEnabled) return;
            var path = e.PathToImage?.LocalPath;
            if (string.IsNullOrEmpty(path)) return;
            var type = e.MetaData?.Image?.ImageType ?? "";
            if (string.Equals(type, "SNAPSHOT", StringComparison.OrdinalIgnoreCase)) return;
            if (Array.IndexOf(FitsSuffixes, Path.GetExtension(path).ToLowerInvariant()) < 0)
            {
                // Bortle stacks FITS only; say it once, not on every frame.
                if (!_warnedFormat)
                {
                    _warnedFormat = true;
                    Notification.ShowWarning(
                        "Bortle: NINA speichert nicht als FITS – diese Bilder werden nicht übertragen. "
                        + "Optionen → Bildgebung → Dateiformat: FITS.");
                }
                return;
            }
            _uploader.FrameSaved(path, e.MetaData?.Target?.Name);
        }
        catch (Exception ex)
        {
            Logger.Error($"Bortle: could not queue a frame: {ex}");
        }
        finally
        {
            // After the frame is queued: once this counts as saved, a session
            // end may be queued behind it.
            _saves.SaveFinished();
        }
    }

    private async Task SubscribeSequenceAsync()
    {
        // The sequencer is created after the plugins; wait for it, as the
        // Advanced API plugin does.
        while (!_sequence.Initialized)
        {
            await Task.Delay(100);
        }
        _sequence.SequenceStarting += OnSequenceStarting;
        _sequence.SequenceFinished += OnSequenceFinished;
    }

    private Task OnSequenceStarting(object sender, EventArgs e)
    {
        try
        {
            if (UploadEnabled) _uploader.SequenceStarted(null);
        }
        catch (Exception ex)
        {
            Logger.Error($"Bortle: could not queue the session start: {ex}");
        }
        return Task.CompletedTask;
    }

    private Task OnSequenceFinished(object sender, EventArgs e)
    {
        try
        {
            // NINA raises this right after the last instruction, while the
            // last image is still being saved; end the session behind it.
            if (UploadEnabled)
            {
                _ = _uploader.EndAfterSavesAsync(TimeSpan.FromMinutes(10), CancellationToken.None)
                    .ContinueWith(
                        t => Logger.Error($"Bortle: could not queue the session end: {t.Exception}"),
                        TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Bortle: could not queue the session end: {ex}");
        }
        return Task.CompletedTask;
    }

    private void OnProfileChanged(object? sender, EventArgs e)
    {
        Raise(nameof(ServerUrl));
        Raise(nameof(PluginKey));
        Raise(nameof(UploadEnabled));
        _uploader.Wake();
    }

    public override async Task Teardown()
    {
        _imaging.ImagePrepared -= OnImagePrepared;
        _images.BeforeImageSaved -= OnBeforeImageSaved;
        _images.ImageSaved -= OnImageSaved;
        _profile.ProfileChanged -= OnProfileChanged;
        _sequence.SequenceStarting -= OnSequenceStarting;
        _sequence.SequenceFinished -= OnSequenceFinished;
        // What is still queued stays on disk and goes out next time NINA runs.
        await _uploader.StopAsync();
        _uploader.Dispose();
        Uploader = null;
        await base.Teardown();
    }

    private void Raise([CallerMemberName] string? name = null) =>
        Application.Current?.Dispatcher?.BeginInvoke(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

    private sealed class NinaLog : ILog
    {
        public void Info(string message) => Logger.Info(message);
        public void Error(string message) => Logger.Error(message);
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Func<Task> _run;
        public RelayCommand(Func<Task> run) => _run = run;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public async void Execute(object? parameter) => await _run();
    }
}
