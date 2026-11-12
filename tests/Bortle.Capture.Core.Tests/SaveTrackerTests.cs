using Xunit;

namespace Bortle.Capture.Tests;

public sealed class SaveTrackerTests
{
    private DateTimeOffset _now = new(2026, 11, 12, 19, 42, 51, TimeSpan.Zero);
    private static readonly TimeSpan Quiet = SaveTracker.DefaultQuiet;

    private SaveTracker Make() => new(() => _now);

    [Fact]
    public void Waiting_starts_a_quiet_period_of_its_own()
    {
        // The exposure just ended; its image has not reached the save queue.
        var saves = Make();
        var since = _now;
        Assert.False(saves.Settled(since, Quiet));
        _now += Quiet;
        Assert.True(saves.Settled(since, Quiet));
    }

    [Fact]
    public void An_image_being_saved_holds_until_it_is_written_and_then_some()
    {
        var saves = Make();
        var since = _now;
        saves.SaveStarted();
        _now += TimeSpan.FromMinutes(1);
        Assert.False(saves.Settled(since, Quiet));
        saves.SaveFinished();
        Assert.Equal(0, saves.InFlight);
        Assert.False(saves.Settled(since, Quiet)); // the quiet counts from the save
        _now += Quiet;
        Assert.True(saves.Settled(since, Quiet));
    }

    [Fact]
    public void A_prepared_image_extends_the_quiet()
    {
        var saves = Make();
        var since = _now;
        _now += Quiet - TimeSpan.FromSeconds(1);
        saves.Activity();
        _now += TimeSpan.FromSeconds(2);
        Assert.False(saves.Settled(since, Quiet));
        _now += Quiet;
        Assert.True(saves.Settled(since, Quiet));
    }

    [Fact]
    public void Two_images_in_flight_need_two_saves()
    {
        var saves = Make();
        var since = _now;
        saves.SaveStarted();
        saves.SaveStarted();
        saves.SaveFinished();
        _now += Quiet;
        Assert.False(saves.Settled(since, Quiet));
        saves.SaveFinished();
        _now += Quiet;
        Assert.True(saves.Settled(since, Quiet));
    }

    [Fact]
    public void A_save_that_never_reports_back_is_given_up()
    {
        // NINA raises no ImageSaved when writing fails; it gives up after 5 min.
        var saves = Make();
        var since = _now;
        saves.SaveStarted();
        _now += SaveTracker.GiveUpAfter;
        Assert.False(saves.Settled(since, Quiet));
        _now += TimeSpan.FromSeconds(1);
        Assert.Equal(0, saves.InFlight);
        Assert.True(saves.Settled(since, Quiet));
    }

    [Fact]
    public void An_image_saved_long_before_does_not_count()
    {
        var saves = Make();
        saves.SaveStarted();
        saves.SaveFinished();
        _now += TimeSpan.FromHours(1);
        var since = _now;
        Assert.False(saves.Settled(since, Quiet));
        _now += Quiet;
        Assert.True(saves.Settled(since, Quiet));
    }
}
