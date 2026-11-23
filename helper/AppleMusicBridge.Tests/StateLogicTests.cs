using AppleMusicBridge;
using Xunit;

namespace AppleMusicBridge.Tests;

public class RepeatModeTests
{
    [Fact]
    public void Cycles_like_the_apple_music_button()
    {
        Assert.Equal(RepeatMode.All, RepeatModes.Next(RepeatMode.Off));
        Assert.Equal(RepeatMode.One, RepeatModes.Next(RepeatMode.All));
        Assert.Equal(RepeatMode.Off, RepeatModes.Next(RepeatMode.One));
    }

    // RepeatMode is internal, so the theory takes its name (public test methods cannot expose internal types).
    [Theory]
    [InlineData("Off", "off")]
    [InlineData("All", "all")]
    [InlineData("One", "one")]
    public void Wire_names(string mode, string expected) =>
        Assert.Equal(expected, RepeatModes.ToWire(Enum.Parse<RepeatMode>(mode)));

    [Theory]
    [InlineData("Playing", "playing")]
    [InlineData("Paused", "paused")]
    [InlineData("Changing", "changing")]
    public void Status_wire_names(string name, string expected) => Assert.Equal(expected, PlaybackStatusText.FromName(name));
}

public class MediaTextTests
{
    [Fact]
    public void Splits_artist_and_album_when_the_album_field_is_empty()
    {
        var media = MediaText.From(" Song ", "Daft Punk — Discovery", "");
        Assert.Equal(new MediaText("Song", "Daft Punk", "Discovery"), media);
    }

    [Fact]
    public void Keeps_the_artist_untouched_when_the_album_is_reported()
    {
        var media = MediaText.From("Song", "A — B", "Real Album");
        Assert.Equal(new MediaText("Song", "A — B", "Real Album"), media);
    }

    [Fact]
    public void Plain_hyphen_is_not_treated_as_a_separator()
    {
        var media = MediaText.From("Song", "Jay-Z - Live", null);
        Assert.Equal(new MediaText("Song", "Jay-Z - Live", ""), media);
    }

    [Fact]
    public void Null_fields_become_empty_strings()
    {
        Assert.Equal(MediaText.Empty, MediaText.From(null, null, null));
    }
}

public class TimelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Paused_position_is_reported_as_is()
    {
        var (position, duration) = Timeline.Compute(
            TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(30), Now.AddSeconds(-10), Now, false, 1.0);
        Assert.Equal(30, position);
        Assert.Equal(200, duration);
    }

    [Fact]
    public void Playing_position_advances_since_the_last_update()
    {
        var (position, _) = Timeline.Compute(
            TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(30), Now.AddSeconds(-10), Now, true, null);
        Assert.Equal(40, position);
    }

    [Fact]
    public void Playback_rate_is_applied()
    {
        var (position, _) = Timeline.Compute(
            TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(30), Now.AddSeconds(-10), Now, true, 2.0);
        Assert.Equal(50, position);
    }

    [Fact]
    public void Position_never_passes_the_end()
    {
        var (position, _) = Timeline.Compute(
            TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(195), Now.AddSeconds(-60), Now, true, 1.0);
        Assert.Equal(200, position);
    }

    [Fact]
    public void Unset_last_update_time_is_ignored()
    {
        var (position, _) = Timeline.Compute(
            TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(30), default, Now, true, 1.0);
        Assert.Equal(30, position);
    }

    [Fact]
    public void Start_offset_is_subtracted_and_unknown_duration_is_zero()
    {
        var (position, duration) = Timeline.Compute(
            TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.FromSeconds(12.34), Now, Now, false, null);
        Assert.Equal(7.3, position);
        Assert.Equal(0, duration);
    }
}

public class StateComparerTests
{
    private static readonly StateMessage Playing = MessageSerializationTests.SampleState() with
    {
        Position = 10,
        Duration = 200,
        IsPlaying = true,
        Timestamp = 1_000_000,
    };

    [Fact]
    public void Position_that_matches_extrapolation_is_not_a_change()
    {
        var next = Playing with { Position = 15.4, Timestamp = 1_005_000 };
        Assert.True(StateComparer.IsEquivalent(Playing, next));
    }

    [Fact]
    public void Seeking_is_a_change()
    {
        var next = Playing with { Position = 90, Timestamp = 1_005_000 };
        Assert.False(StateComparer.IsEquivalent(Playing, next));
    }

    [Fact]
    public void Paused_position_must_stay_put()
    {
        var paused = Playing with { IsPlaying = false, Status = "paused" };
        Assert.True(StateComparer.IsEquivalent(paused, paused with { Timestamp = 1_060_000 }));
        Assert.False(StateComparer.IsEquivalent(paused, paused with { Position = 20, Timestamp = 1_060_000 }));
    }

    [Fact]
    public void Any_other_field_is_a_change()
    {
        Assert.False(StateComparer.IsEquivalent(Playing, Playing with { Title = "Other" }));
        Assert.False(StateComparer.IsEquivalent(Playing, Playing with { IsPlaying = false }));
        Assert.False(StateComparer.IsEquivalent(Playing, Playing with { Shuffle = true }));
        Assert.False(StateComparer.IsEquivalent(Playing, Playing with { Repeat = "one" }));
        Assert.False(StateComparer.IsEquivalent(Playing, Playing with { AlbumArtId = "ffff" }));
        Assert.False(StateComparer.IsEquivalent(Playing, Playing with { AppRunning = false }));
        Assert.False(StateComparer.IsEquivalent(
            Playing,
            Playing with { Supports = Playing.Supports with { Shuffle = true } }));
    }

    [Fact]
    public void The_art_payload_itself_does_not_count()
    {
        Assert.True(StateComparer.IsEquivalent(Playing, Playing with { AlbumArt = "abc" }));
    }
}

public class OptionsTests
{
    [Fact]
    public void Defaults()
    {
        Assert.True(Options.TryParse([], out var options, out _));
        Assert.Equal(new Options(false, false, false, null), options);
    }

    [Fact]
    public void All_flags()
    {
        Assert.True(Options.TryParse(["--debug", "--parent-pid", "1234", "--list-sessions"], out var options, out _));
        Assert.Equal(new Options(true, true, false, 1234), options);
    }

    [Theory]
    [InlineData("--parent-pid")]
    [InlineData("--parent-pid", "abc")]
    [InlineData("--parent-pid", "-5")]
    [InlineData("--verbose")]
    public void Invalid_arguments_are_reported(params string[] args)
    {
        Assert.False(Options.TryParse(args, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}

public class AppleMusicIdentityTests
{
    [Theory]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")]
    [InlineData("appleinc.applemusicwin_nzyj5cx40ttqa!app")]
    public void Matches_the_apple_music_session(string id) => Assert.True(AppleMusicIdentity.IsAppleMusicSession(id));

    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic")]
    [InlineData("AppleInc.AppleTVWin_nzyj5cx40ttqa!App")]
    [InlineData("iTunes")]
    [InlineData("AppleMusic")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App2")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_every_other_session(string? id) => Assert.False(AppleMusicIdentity.IsAppleMusicSession(id));
}
