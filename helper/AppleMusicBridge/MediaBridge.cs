using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace AppleMusicBridge;

[Flags]
internal enum RefreshReason
{
    None = 0,

    /// <summary>The list of media sessions changed (an app registered or removed its session).</summary>
    Sessions = 1,

    /// <summary>Title, artist, album or cover changed.</summary>
    Media = 2,

    /// <summary>Play state, shuffle, repeat or the set of enabled controls changed.</summary>
    Playback = 4,

    /// <summary>Position or duration changed.</summary>
    Timeline = 8,

    /// <summary>Apple Music's process may have started or stopped: re-check appRunning.</summary>
    Process = 16,

    /// <summary>Send the state even if nothing changed, including the album art.</summary>
    Force = 32,
}

/// <summary>
/// Watches the Apple Music media session through Windows' System Media Transport Controls and executes
/// playback commands on it. Entirely event driven: nothing happens until Windows raises an event or a
/// command arrives. Bursts of events (a track change raises several) are coalesced into one update.
/// </summary>
internal sealed class MediaBridge : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ArtRetryDelay = TimeSpan.FromSeconds(1.5);
    private const int MaxConsecutiveFailures = 3;

    private readonly ProtocolWriter _output;
    private readonly bool _debug;
    private readonly bool _abbreviateArt;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly TypedEventHandler<SessionManager, SessionsChangedEventArgs> _onSessionsChanged;
    private readonly TypedEventHandler<Session, MediaPropertiesChangedEventArgs> _onMediaPropertiesChanged;
    private readonly TypedEventHandler<Session, PlaybackInfoChangedEventArgs> _onPlaybackInfoChanged;
    private readonly TypedEventHandler<Session, TimelinePropertiesChangedEventArgs> _onTimelinePropertiesChanged;

    private int _pendingReasons;

    // Kept in fields on purpose: WinRT objects that are only referenced by event subscriptions can be
    // garbage collected, after which their events silently stop.
    private SessionManager? _manager;

    // Written only by the refresh loop, read by commands.
    private volatile Session? _session;
    private volatile string? _sessionId;

    // Only touched by the refresh loop.
    private MediaText _media = MediaText.Empty;
    private AlbumArtImage? _art;
    private string? _artRetryKey;
    private bool _lastIsPlaying;
    private StateMessage? _lastSent;
    private string? _lastSentArtId;
    private int _consecutiveFailures;

    // Shuffle and repeat through Apple Music's own buttons (its media session does not offer them).
    private readonly PlayerControls _controls;

    /// <param name="abbreviateArt">Shorten albumArt in the output (stdout is a terminal, not the plugin).</param>
    public MediaBridge(ProtocolWriter output, bool debug, bool abbreviateArt)
    {
        _output = output;
        _debug = debug;
        _abbreviateArt = abbreviateArt;
        _onSessionsChanged = (_, _) => OnEvent(RefreshReason.Sessions, "SessionsChanged");
        _onMediaPropertiesChanged = (_, _) => OnEvent(RefreshReason.Media, "MediaPropertiesChanged");
        _onPlaybackInfoChanged = (_, _) => OnEvent(RefreshReason.Playback, "PlaybackInfoChanged");
        _onTimelinePropertiesChanged = (_, _) => OnEvent(RefreshReason.Timeline, "TimelinePropertiesChanged");
        _controls = new PlayerControls(() => OnEvent(RefreshReason.Playback, "Shuffle/repeat button changed"));
    }

    public async Task StartAsync()
    {
        _manager = await SessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        _manager.SessionsChanged += _onSessionsChanged;
        _ = Task.Run(RefreshLoopAsync);
        Request(RefreshReason.Sessions | RefreshReason.Process | RefreshReason.Force);
        Log.Info("Listening for Windows media session events.");
    }

    /// <summary>Schedules a state update. Safe to call from any thread; calls are coalesced.</summary>
    public void Request(RefreshReason reasons)
    {
        Interlocked.Or(ref _pendingReasons, (int)reasons);
        _wake.Release();
    }

    public async Task<ResultMessage> ExecuteAsync(Command command)
    {
        try
        {
            switch (command.Name)
            {
                case Commands.Ping:
                    return ResultMessage.Success(command);

                case Commands.Refresh:
                    Request(RefreshReason.Sessions | RefreshReason.Process | RefreshReason.Media | RefreshReason.Force);
                    return ResultMessage.Success(command);

                case Commands.Focus:
                {
                    var activation = WindowActivator.BringToFront(_sessionId ?? AppleMusicIdentity.SessionId);
                    if (activation.Ok)
                    {
                        Log.Debug($"Focus: {activation.Detail}");
                        return ResultMessage.Success(command);
                    }

                    Log.Info($"Focus failed ({activation.Error}): {activation.Detail}");
                    return ResultMessage.Failure(command, activation.Error ?? ErrorCodes.Failed);
                }

                case Commands.ToggleShuffle:
                case Commands.CycleRepeat:
                    return await ShuffleOrRepeatAsync(command);
            }

            var session = _session;
            if (session is null)
            {
                var error = AppleMusicIdentity.IsAppleMusicProcessRunning() ? ErrorCodes.NoSession : ErrorCodes.NotRunning;
                Log.Info($"Command '{command.Name}' ignored: {error}.");
                return ResultMessage.Failure(command, error);
            }

            bool accepted;
            var refresh = RefreshReason.Playback;
            switch (command.Name)
            {
                case Commands.Play:
                    accepted = await Call(session.TryPlayAsync());
                    break;

                case Commands.Pause:
                    accepted = await Call(session.TryPauseAsync());
                    break;

                case Commands.TogglePlayPause:
                {
                    var info = session.GetPlaybackInfo();
                    if (info.Controls.IsPlayPauseToggleEnabled)
                    {
                        accepted = await Call(session.TryTogglePlayPauseAsync());
                    }
                    else if (info.PlaybackStatus == PlaybackStatus.Playing)
                    {
                        accepted = await Call(session.TryPauseAsync());
                    }
                    else
                    {
                        accepted = await Call(session.TryPlayAsync());
                    }

                    break;
                }

                case Commands.Next:
                    accepted = await Call(session.TrySkipNextAsync());
                    refresh |= RefreshReason.Media;
                    break;

                case Commands.Previous:
                    accepted = await Call(session.TrySkipPreviousAsync());
                    refresh |= RefreshReason.Media;
                    break;

                default:
                    return ResultMessage.Failure(command, ErrorCodes.UnknownCommand);
            }

            // Apple Music normally raises events for the change; this covers the case where it is slow to.
            Request(refresh);

            if (!accepted)
            {
                Log.Info($"Apple Music rejected '{command.Name}'.");
                return ResultMessage.Failure(command, ErrorCodes.Rejected);
            }

            return ResultMessage.Success(command);
        }
        catch (TimeoutException)
        {
            Log.Warn($"Command '{command.Name}' timed out.");
            return ResultMessage.Failure(command, ErrorCodes.Timeout);
        }
        catch (Exception ex)
        {
            Log.Warn($"Command '{command.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            Request(RefreshReason.Sessions);
            return ResultMessage.Failure(command, ErrorCodes.Failed);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        DetachSession();
        _controls.Dispose();
        if (_manager is not null)
        {
            try
            {
                _manager.SessionsChanged -= _onSessionsChanged;
            }
            catch (Exception)
            {
                // Shutting down anyway.
            }
        }
    }

    /// <summary>
    /// Shuffle/repeat: through the media session when it supports them, otherwise by pressing Apple Music's own
    /// player-bar button through UI Automation (Apple Music for Windows does not offer them to the media session).
    /// </summary>
    private async Task<ResultMessage> ShuffleOrRepeatAsync(Command command)
    {
        var shuffle = command.Name == Commands.ToggleShuffle;
        var session = _session;
        if (session is not null)
        {
            var info = session.GetPlaybackInfo();
            if (shuffle && info.Controls.IsShuffleEnabled)
            {
                return Finish(command, await Call(session.TryChangeShuffleActiveAsync(!(info.IsShuffleActive ?? false))));
            }

            if (!shuffle && info.Controls.IsRepeatEnabled)
            {
                var current = info.AutoRepeatMode is { } mode ? FromWinRt(mode) : RepeatMode.Off;
                return Finish(command, await Call(session.TryChangeAutoRepeatModeAsync(ToWinRt(RepeatModes.Next(current)))));
            }
        }
        else if (!AppleMusicIdentity.IsAppleMusicProcessRunning())
        {
            return ResultMessage.Failure(command, ErrorCodes.NotRunning);
        }

        var appId = _sessionId ?? AppleMusicIdentity.SessionId;
        var result = shuffle ? _controls.ToggleShuffle(appId) : _controls.CycleRepeat(appId);
        if (!result.Ok)
        {
            Log.Info($"Command '{command.Name}' failed: {result.Error}.");
            return ResultMessage.Failure(command, result.Error ?? ErrorCodes.Failed);
        }

        // The button updates asynchronously; its change event normally triggers the update, this is a fallback.
        _ = Task.Delay(TimeSpan.FromMilliseconds(300), _cts.Token).ContinueWith(
            delay =>
            {
                if (!delay.IsCanceled)
                {
                    Request(RefreshReason.Playback);
                }
            },
            TaskScheduler.Default);
        return ResultMessage.Success(command);
    }

    private ResultMessage Finish(Command command, bool accepted)
    {
        Request(RefreshReason.Playback);
        if (accepted)
        {
            return ResultMessage.Success(command);
        }

        Log.Info($"Apple Music rejected '{command.Name}'.");
        return ResultMessage.Failure(command, ErrorCodes.Rejected);
    }

    private static Task<bool> Call(IAsyncOperation<bool> operation) => operation.AsTask().WaitAsync(CallTimeout);

    private void OnEvent(RefreshReason reason, string name)
    {
        Log.Debug($"Event: {name}");
        Request(reason);
    }

    private async Task RefreshLoopAsync()
    {
        var cancellationToken = _cts.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(cancellationToken);

                // Let the burst settle, then handle everything that arrived in one pass.
                await Task.Delay(DebounceDelay, cancellationToken);
                while (_wake.Wait(0))
                {
                }

                var reasons = (RefreshReason)Interlocked.Exchange(ref _pendingReasons, 0);
                if (reasons != RefreshReason.None)
                {
                    await RefreshAsync(reasons, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"State update failed: {ex}");
            }
        }
    }

    private async Task RefreshAsync(RefreshReason reasons, CancellationToken cancellationToken)
    {
        Log.Debug($"Updating state ({reasons}).");

        if (_debug && (reasons & (RefreshReason.Sessions | RefreshReason.Process)) != 0)
        {
            await LogDiagnosticsAsync(reasons);
        }

        var sessionChanged = (reasons & (RefreshReason.Sessions | RefreshReason.Process | RefreshReason.Force)) != 0
            && BindSession();
        var force = reasons.HasFlag(RefreshReason.Force);

        var session = _session;
        if (session is null)
        {
            _media = MediaText.Empty;
            _art = null;
            var appRunning = AppleMusicIdentity.IsAppleMusicProcessRunning();
            // Without a media session the player-bar buttons may still be there (app open, nothing loaded).
            var buttons = appRunning ? _controls.Read(AppleMusicIdentity.SessionId) : PlayerControlsState.None;
            Emit(
                new StateMessage
                {
                    AppRunning = appRunning,
                    HasSession = false,
                    Status = PlaybackStatusText.None,
                    Shuffle = buttons.Shuffle,
                    Repeat = buttons.Repeat is { } repeat ? RepeatModes.ToWire(repeat) : null,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Supports = Capabilities.None with { Shuffle = buttons.HasShuffle, Repeat = buttons.HasRepeat },
                },
                force);
            return;
        }

        StateMessage state;
        try
        {
            if (sessionChanged || (reasons & (RefreshReason.Media | RefreshReason.Force)) != 0)
            {
                await ReadMediaAsync(session, cancellationToken);
            }

            state = ReadState(session);
            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            Log.Warn($"Reading the Apple Music session failed: {ex.GetType().Name}: {ex.Message}");
            if (_consecutiveFailures <= MaxConsecutiveFailures)
            {
                // Most likely the app just closed; look at the session list again.
                DetachSession();
                Request(RefreshReason.Sessions);
            }

            return;
        }

        Emit(state, force);
    }

    /// <summary>Finds the Apple Music session and (re)subscribes to its events. True if it changed.</summary>
    private bool BindSession()
    {
        if (_manager is null)
        {
            return false;
        }

        IReadOnlyList<Session> sessions;
        try
        {
            sessions = _manager.GetSessions();
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing media sessions failed: {ex.Message}");
            sessions = [];
        }

        Session? match = null;
        string? matchId = null;
        var ids = new List<string>();
        foreach (var candidate in sessions)
        {
            string id;
            try
            {
                id = candidate.SourceAppUserModelId ?? "";
            }
            catch (Exception)
            {
                continue;
            }

            ids.Add(id);
            if (match is null && AppleMusicIdentity.IsAppleMusicSession(id))
            {
                match = candidate;
                matchId = id;
            }
        }

        Log.Debug($"Session IDs: [{string.Join(", ", ids.Select(id => $"\"{id}\""))}] -> {(matchId is null ? "no Apple Music session" : $"matched \"{matchId}\"")}");

        if (ReferenceEquals(match, _session))
        {
            return false;
        }

        DetachSession();
        _lastIsPlaying = false;

        if (match is null)
        {
            Log.Info("No Apple Music media session.");
            return true;
        }

        try
        {
            match.MediaPropertiesChanged += _onMediaPropertiesChanged;
            match.PlaybackInfoChanged += _onPlaybackInfoChanged;
            match.TimelinePropertiesChanged += _onTimelinePropertiesChanged;
        }
        catch (Exception ex)
        {
            Log.Warn($"Subscribing to the Apple Music session failed: {ex.Message}");
        }

        _session = match;
        _sessionId = matchId;
        Log.Info($"Apple Music media session found: \"{matchId}\".");
        return true;
    }

    private void DetachSession()
    {
        var old = _session;
        _session = null;
        _sessionId = null;
        if (old is null)
        {
            return;
        }

        try
        {
            old.MediaPropertiesChanged -= _onMediaPropertiesChanged;
            old.PlaybackInfoChanged -= _onPlaybackInfoChanged;
            old.TimelinePropertiesChanged -= _onTimelinePropertiesChanged;
        }
        catch (Exception ex)
        {
            Log.Debug($"Unsubscribing from the old session failed (it is probably gone): {ex.Message}");
        }
    }

    private async Task ReadMediaAsync(Session session, CancellationToken cancellationToken)
    {
        var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken).WaitAsync(CallTimeout, cancellationToken);
        if (properties is null)
        {
            Log.Debug("Media properties: none.");
            _media = MediaText.Empty;
            _art = null;
            return;
        }

        if (_debug)
        {
            Log.Debug(
                $"Media properties: title=\"{properties.Title}\" artist=\"{properties.Artist}\" " +
                $"albumTitle=\"{properties.AlbumTitle}\" albumArtist=\"{properties.AlbumArtist}\" " +
                $"subtitle=\"{properties.Subtitle}\" thumbnail={(properties.Thumbnail is null ? "no" : "yes")}");
        }

        var media = MediaText.From(properties.Title, properties.Artist, properties.AlbumTitle);
        if (media != _media && media.Title.Length > 0)
        {
            Log.Info($"Now playing: \"{media.Title}\" by \"{media.Artist}\"{(media.Album.Length > 0 ? $" from \"{media.Album}\"" : "")}.");
        }

        _media = media;
        _art = await AlbumArt.TryCreateAsync(properties.Thumbnail, cancellationToken);

        if (_art is null && properties.Thumbnail is not null)
        {
            ScheduleArtRetry(media);
        }

        if (_debug && _art is not null && _art.Id != _lastSentArtId)
        {
            SaveDebugCover(_art);
        }
    }

    /// <summary>Thumbnails are sometimes not ready right after a track change: retry once per track.</summary>
    private void ScheduleArtRetry(MediaText media)
    {
        var key = $"{media.Title}\u0001{media.Artist}\u0001{media.Album}";
        if (key == _artRetryKey)
        {
            return;
        }

        _artRetryKey = key;
        Log.Debug($"Album art not readable yet; retrying once in {ArtRetryDelay.TotalSeconds:0.#} s.");
        _ = Task.Delay(ArtRetryDelay, _cts.Token).ContinueWith(
            delay =>
            {
                if (!delay.IsCanceled)
                {
                    Request(RefreshReason.Media);
                }
            },
            TaskScheduler.Default);
    }

    private StateMessage ReadState(Session session)
    {
        var info = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var controls = info.Controls;
        var status = info.PlaybackStatus;

        // Apple Music for Windows does not offer shuffle/repeat through its media session; read them from its
        // player-bar buttons instead (only when the session does not report them itself).
        var buttons = controls.IsShuffleEnabled && controls.IsRepeatEnabled
            ? PlayerControlsState.None
            : _controls.Read(_sessionId);
        var shuffle = controls.IsShuffleEnabled ? info.IsShuffleActive : buttons.Shuffle;
        RepeatMode? repeat = controls.IsRepeatEnabled
            ? info.AutoRepeatMode is { } autoRepeat ? FromWinRt(autoRepeat) : null
            : buttons.Repeat;

        var isPlaying = status switch
        {
            PlaybackStatus.Playing => true,
            // A track change is in progress: keep the last value so the play/pause key does not flicker.
            PlaybackStatus.Changing => _lastIsPlaying,
            _ => false,
        };
        _lastIsPlaying = isPlaying;

        var now = DateTimeOffset.UtcNow;
        var (position, duration) = Timeline.Compute(
            timeline.StartTime,
            timeline.EndTime,
            timeline.Position,
            timeline.LastUpdatedTime,
            now,
            isPlaying,
            info.PlaybackRate);

        if (_debug)
        {
            Log.Debug(
                $"Playback info: status={status} isShuffleActive={Show(info.IsShuffleActive)} " +
                $"autoRepeatMode={Show(info.AutoRepeatMode)} playbackRate={Show(info.PlaybackRate)} | controls: " +
                $"play={controls.IsPlayEnabled} pause={controls.IsPauseEnabled} toggle={controls.IsPlayPauseToggleEnabled} " +
                $"next={controls.IsNextEnabled} previous={controls.IsPreviousEnabled} " +
                $"shuffle={controls.IsShuffleEnabled} repeat={controls.IsRepeatEnabled}");
            Log.Debug(
                $"Timeline: start={timeline.StartTime} end={timeline.EndTime} position={timeline.Position} " +
                $"lastUpdated={timeline.LastUpdatedTime:O}");
        }

        return new StateMessage
        {
            AppRunning = true,
            HasSession = true,
            Title = _media.Title,
            Artist = _media.Artist,
            Album = _media.Album,
            IsPlaying = isPlaying,
            Status = PlaybackStatusText.FromName(status.ToString()),
            Shuffle = shuffle,
            Repeat = repeat is { } mode ? RepeatModes.ToWire(mode) : null,
            Position = position,
            Duration = duration,
            Timestamp = now.ToUnixTimeMilliseconds(),
            AlbumArtId = _art?.Id,
            Supports = new Capabilities(
                Play: controls.IsPlayEnabled,
                Pause: controls.IsPauseEnabled,
                PlayPause: controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                Next: controls.IsNextEnabled,
                Previous: controls.IsPreviousEnabled,
                Shuffle: controls.IsShuffleEnabled || buttons.HasShuffle,
                Repeat: controls.IsRepeatEnabled || buttons.HasRepeat),
            SourceAppId = _sessionId,
        };
    }

    private void Emit(StateMessage state, bool force)
    {
        var artChanged = !string.Equals(state.AlbumArtId, _lastSentArtId, StringComparison.Ordinal);
        if (!force && !artChanged && _lastSent is not null && StateComparer.IsEquivalent(_lastSent, state))
        {
            Log.Debug("No meaningful change; nothing sent.");
            return;
        }

        var message = state;
        if ((artChanged || force) && _art is not null && _art.Id == state.AlbumArtId)
        {
            message = state with { AlbumArt = _abbreviateArt ? Abbreviate(_art.Base64) : _art.Base64 };
        }

        _output.Send(message);
        _lastSent = state;
        _lastSentArtId = state.AlbumArtId;
        Log.Debug($"Sent state: {Describe(state)}{(message.AlbumArt is null ? "" : " + album art")}.");
    }

    private async Task LogDiagnosticsAsync(RefreshReason reasons)
    {
        if (_manager is null)
        {
            return;
        }

        if (reasons.HasFlag(RefreshReason.Sessions))
        {
            Log.Debug("All media sessions (SourceAppUserModelId):");
            foreach (var line in await Diagnostics.DescribeSessionsAsync(_manager))
            {
                Log.Debug("  " + line);
            }
        }

        if (reasons.HasFlag(RefreshReason.Process))
        {
            Log.Debug("Processes with \"apple\" or \"music\" in their name:");
            foreach (var line in Diagnostics.DescribeProcesses())
            {
                Log.Debug("  " + line);
            }
        }
    }

    private static void SaveDebugCover(AlbumArtImage art)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "AppleMusicBridge");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "cover.png");
            File.WriteAllBytes(path, art.Png);
            Log.Debug($"Cover {art.Id} ({art.Png.Length} bytes, {AlbumArt.Size}x{AlbumArt.Size} PNG) saved to {path}");
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not save the debug cover: {ex.Message}");
        }
    }

    private static RepeatMode FromWinRt(MediaPlaybackAutoRepeatMode mode) => mode switch
    {
        MediaPlaybackAutoRepeatMode.List => RepeatMode.All,
        MediaPlaybackAutoRepeatMode.Track => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    private static MediaPlaybackAutoRepeatMode ToWinRt(RepeatMode mode) => mode switch
    {
        RepeatMode.All => MediaPlaybackAutoRepeatMode.List,
        RepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
        _ => MediaPlaybackAutoRepeatMode.None,
    };

    private static string Abbreviate(string base64) =>
        $"{base64[..Math.Min(24, base64.Length)]}... [{base64.Length} base64 chars, shortened because stdout is a terminal]";

    private static string Show<T>(T? value)
        where T : struct => value?.ToString() ?? "null";

    private static string Describe(StateMessage s) =>
        $"appRunning={s.AppRunning} hasSession={s.HasSession} status={s.Status} isPlaying={s.IsPlaying} " +
        $"\"{s.Title}\" / \"{s.Artist}\" / \"{s.Album}\" shuffle={s.Shuffle?.ToString() ?? "null"} " +
        $"repeat={s.Repeat ?? "null"} position={s.Position}/{s.Duration} art={s.AlbumArtId ?? "none"}";
}
