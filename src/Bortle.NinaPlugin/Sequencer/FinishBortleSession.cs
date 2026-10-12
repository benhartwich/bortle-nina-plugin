using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;

namespace Bortle.NinaPlugin.Sequencer;

/// <summary>
/// "Finish Bortle session": the last instruction of a night, after the park.
///
/// It waits until the frames still on their way have reached Bortle — at most
/// <see cref="UploadTimeoutMinutes"/> — and then reports the end, which starts
/// the development. It never fails and never throws except when the user stops
/// the sequence: a server that is down costs the wait, never the night, and
/// what did not go out follows later from the queue.
///
/// Bortle's own sequence generator puts it in (services/planner/nina_sequence.py)
/// when NINA lists this plugin; the type name and the property are that
/// contract.
/// </summary>
[ExportMetadata("Name", "Finish Bortle session")]
[ExportMetadata("Description", "Waits for the last frames to reach Bortle, then reports the end of the session so Bortle develops the night. Place it last, after parking. Never fails the sequence.")]
[ExportMetadata("Icon", "ScriptSVG")]
[ExportMetadata("Category", "Bortle")]
[Export(typeof(ISequenceItem))]
[JsonObject(MemberSerialization.OptIn)]
public sealed class FinishBortleSession : SequenceItem
{
    private int _uploadTimeoutMinutes = 30;

    [ImportingConstructor]
    public FinishBortleSession()
    {
    }

    [JsonProperty]
    public int UploadTimeoutMinutes
    {
        get => _uploadTimeoutMinutes;
        set
        {
            _uploadTimeoutMinutes = Math.Clamp(value, 0, 24 * 60);
            RaisePropertyChanged();
        }
    }

    public override object Clone() => new FinishBortleSession
    {
        Icon = Icon,
        Name = Name,
        Category = Category,
        Description = Description,
        UploadTimeoutMinutes = UploadTimeoutMinutes,
    };

    public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
    {
        var uploader = BortlePlugin.Uploader;
        if (uploader is null)
        {
            Logger.Warning("Bortle: plugin not running, session end not reported");
            return;
        }
        try
        {
            progress?.Report(new ApplicationStatus
            {
                Status = $"Bortle: {uploader.Status.Waiting} Bilder werden noch übertragen",
            });
            var reached = await uploader.FinishSessionAsync(TimeSpan.FromMinutes(UploadTimeoutMinutes), token);
            if (!reached)
            {
                Logger.Warning(
                    $"Bortle: frames still uploading after {UploadTimeoutMinutes} min; "
                    + "the session end follows them from the queue");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Logger.Error($"Bortle: finishing the session failed, the sequence carries on: {e}");
        }
        finally
        {
            progress?.Report(new ApplicationStatus { Status = "" });
        }
    }

    public override string ToString() =>
        $"Category: {Category}, Item: {nameof(FinishBortleSession)}, UploadTimeoutMinutes: {UploadTimeoutMinutes}";
}
