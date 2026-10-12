using System.Text;
using Xunit;

namespace Bortle.Capture.Tests;

/// <summary>
/// The real client against a real Bortle: a night of two frames, start to end.
/// Needs a running orchestrator and a plugin key for it:
///
///     BORTLE_E2E_URL=http://127.0.0.1:58000 BORTLE_E2E_KEY=bnp_... \
///       dotnet test --filter Category=E2E
///
/// Left out of the normal run (CI filters it) because it needs that server.
/// </summary>
[Trait("Category", "E2E")]
public sealed class LiveServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bortle-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The smallest FITS frame NINA could have written: 8x8, 16 bit.</summary>
    private string Fits(string name, string target)
    {
        var cards = new[]
        {
            "SIMPLE  =                    T",
            "BITPIX  =                   16",
            "NAXIS   =                    2",
            "NAXIS1  =                    8",
            "NAXIS2  =                    8",
            "IMAGETYP= 'LIGHT'",
            $"OBJECT  = '{target}'",
            "EXPTIME =                300.0",
            "END",
        };
        var header = new StringBuilder();
        foreach (var card in cards) header.Append(card.PadRight(80));
        while (header.Length % 2880 != 0) header.Append(' ');
        var data = new byte[2880];
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(header.ToString()).Concat(data).ToArray());
        return path;
    }

    [Fact]
    public async Task A_night_reaches_a_real_server()
    {
        var url = Environment.GetEnvironmentVariable("BORTLE_E2E_URL")
            ?? throw new InvalidOperationException("set BORTLE_E2E_URL and BORTLE_E2E_KEY");
        var key = Environment.GetEnvironmentVariable("BORTLE_E2E_KEY") ?? "";
        var client = new BortleClient(new HttpClient(), () => new ClientSettings(url, key));

        var hello = await client.HelloAsync(CancellationToken.None);
        Assert.Equal(Outcome.Ok, hello.Outcome);
        Assert.False(string.IsNullOrEmpty(hello.Value?.Owner));

        var log = new NullLog();
        using var uploader = new Uploader(new FrameQueue(Path.Combine(_dir, "q")), client, log);
        uploader.SequenceStarted("e2e");
        uploader.FrameSaved(Fits("E2E_0001.fits", "E2E Test"), "E2E Test");
        uploader.FrameSaved(Fits("E2E_0002.fits", "E2E Test"), null); // OBJECT names it
        uploader.Start();
        try
        {
            Assert.True(await uploader.FinishSessionAsync(TimeSpan.FromSeconds(60), CancellationToken.None),
                uploader.Status.Message);
        }
        finally
        {
            await uploader.StopAsync();
        }
        Assert.Empty(log.Errors);
        Assert.Equal(0, uploader.Status.Waiting);
        Assert.Null(uploader.Status.SessionId); // ended
    }
}
