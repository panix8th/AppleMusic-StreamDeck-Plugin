namespace AppleMusicBridge;

// Pure helpers used to build state messages. No Windows APIs here: also compiled into the unit tests.

internal enum RepeatMode
{
    Off,
    All,
    One,
}

internal static class RepeatModes
{
    /// <summary>Same order as Apple Music's own repeat button: off -> all -> one -> off.</summary>
    public static RepeatMode Next(RepeatMode current) => current switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    public static string ToWire(RepeatMode mode) => mode switch
    {
        RepeatMode.All => "all",
        RepeatMode.One => "one",
        _ => "off",
    };
}

internal static class PlaybackStatusText
{
    /// <summary>Status used when there is no Apple Music session.</summary>
    public const string None = "none";

    /// <summary>Maps a status enum name ("Playing", "Paused", ...) to its wire form ("playing", ...).</summary>
    public static string FromName(string statusName) => statusName.ToLowerInvariant();
}

internal readonly record struct MediaText(string Title, string Artist, string Album)
{
    public static readonly MediaText Empty = new("", "", "");

    // Some players (reportedly Apple Music for Windows among them) publish "Artist — Album" in the artist
    // field and leave the album empty. Only split when the album field is empty.
    private const string ArtistAlbumSeparator = " — ";

    public static MediaText From(string? title, string? artist, string? album)
    {
        var cleanTitle = title?.Trim() ?? "";
        var cleanArtist = artist?.Trim() ?? "";
        var cleanAlbum = album?.Trim() ?? "";

        if (cleanAlbum.Length == 0)
        {
            var index = cleanArtist.IndexOf(ArtistAlbumSeparator, StringComparison.Ordinal);
            if (index > 0)
            {
                cleanAlbum = cleanArtist[(index + ArtistAlbumSeparator.Length)..].Trim();
                cleanArtist = cleanArtist[..index].Trim();
            }
        }

        return new MediaText(cleanTitle, cleanArtist, cleanAlbum);
    }
}

internal static class Timeline
{
    /// <summary>
    /// Position and duration in seconds (rounded to 0.1 s). SMTC reports the position at
    /// <paramref name="lastUpdated"/>, so while playing the elapsed time since then is added.
    /// </summary>
    public static (double Position, double Duration) Compute(
        TimeSpan start,
        TimeSpan end,
        TimeSpan position,
        DateTimeOffset lastUpdated,
        DateTimeOffset now,
        bool isPlaying,
        double? playbackRate)
    {
        var duration = Math.Max(0, (end - start).TotalSeconds);
        var seconds = Math.Max(0, (position - start).TotalSeconds);

        if (isPlaying)
        {
            var elapsed = (now - lastUpdated).TotalSeconds;
            // Ignore nonsense timestamps (never set, clock jumps).
            if (elapsed > 0 && elapsed < TimeSpan.FromDays(1).TotalSeconds)
            {
                var rate = playbackRate is > 0 ? playbackRate.Value : 1.0;
                seconds += elapsed * rate;
            }
        }

        if (duration > 0)
        {
            seconds = Math.Min(seconds, duration);
        }

        return (Math.Round(seconds, 1), Math.Round(duration, 1));
    }
}

internal static class StateComparer
{
    /// <summary>A position this close to where playback is expected to be is not worth an update.</summary>
    public const double PositionToleranceSeconds = 1.5;

    /// <summary>
    /// True when <paramref name="next"/> tells the plugin nothing new: every field is equal and the
    /// position is where extrapolating <paramref name="previous"/> would put it.
    /// </summary>
    public static bool IsEquivalent(StateMessage previous, StateMessage next)
    {
        var a = previous with { Position = 0, Timestamp = 0, AlbumArt = null };
        var b = next with { Position = 0, Timestamp = 0, AlbumArt = null };
        if (a != b)
        {
            return false;
        }

        var expected = previous.Position;
        if (previous.IsPlaying)
        {
            expected += Math.Max(0, next.Timestamp - previous.Timestamp) / 1000.0;
            if (previous.Duration > 0)
            {
                expected = Math.Min(expected, previous.Duration);
            }
        }

        return Math.Abs(expected - next.Position) < PositionToleranceSeconds;
    }
}
