using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppleMusicBridge;

// Wire format shared with the Stream Deck plugin (see docs/PROTOCOL.md): one JSON object per line.
//   helper -> plugin (stdout): hello, state, result
//   plugin -> helper (stdin):  {"id": <any JSON value>, "cmd": "<command>"}
// This file must not use Windows APIs: it is also compiled into the unit tests, which run on any OS.

internal static class ProtocolJson
{
    /// <summary>
    /// camelCase, single line. The default encoder escapes every non-ASCII character (\uXXXX), so the
    /// stream is pure ASCII and cannot be garbled by console or pipe code pages.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serialize(object message) =>
        JsonSerializer.Serialize(message, message.GetType(), Options);

    public static byte[] SerializeToUtf8Bytes(object message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Options);
}

/// <summary>First line the helper writes after starting.</summary>
internal sealed record HelloMessage(string Version, int Pid, bool Debug)
{
    [JsonPropertyOrder(-1)]
    public string Type => "hello";
}

/// <summary>Full Apple Music state. Sent whenever something meaningful changed.</summary>
internal sealed record StateMessage
{
    [JsonPropertyOrder(-1)]
    public string Type => "state";

    /// <summary>Apple Music has a media session, or its process is running.</summary>
    public bool AppRunning { get; init; }

    /// <summary>Apple Music has registered a media session (needed for any playback command).</summary>
    public bool HasSession { get; init; }

    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public bool IsPlaying { get; init; }

    /// <summary>none | closed | opened | changing | stopped | playing | paused</summary>
    public string Status { get; init; } = PlaybackStatusText.None;

    /// <summary>null when the session does not report a shuffle state.</summary>
    public bool? Shuffle { get; init; }

    /// <summary>off | all | one, or null when the session does not report a repeat mode.</summary>
    public string? Repeat { get; init; }

    /// <summary>Seconds, sampled at <see cref="Timestamp"/>. Extrapolate while <see cref="IsPlaying"/>.</summary>
    public double Position { get; init; }

    /// <summary>Seconds; 0 when unknown.</summary>
    public double Duration { get; init; }

    /// <summary>Unix time in milliseconds at which <see cref="Position"/> was sampled.</summary>
    public long Timestamp { get; init; }

    /// <summary>Identifies the current cover; null = no cover. Always sent.</summary>
    public string? AlbumArtId { get; init; }

    /// <summary>
    /// 144x144 PNG, base64. Only sent when <see cref="AlbumArtId"/> changed (or after a refresh command);
    /// otherwise omitted and the receiver keeps the cover it already has for that id.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AlbumArt { get; init; }

    public Capabilities Supports { get; init; } = Capabilities.None;

    /// <summary>SourceAppUserModelId of the matched session (diagnostics).</summary>
    public string? SourceAppId { get; init; }
}

/// <summary>Which controls the Apple Music session reports as enabled.</summary>
internal sealed record Capabilities(
    bool Play,
    bool Pause,
    bool PlayPause,
    bool Next,
    bool Previous,
    bool Shuffle,
    bool Repeat)
{
    public static readonly Capabilities None = new(false, false, false, false, false, false, false);
}

/// <summary>Answer to one command. <see cref="Id"/> echoes the command's id (any JSON value).</summary>
internal sealed record ResultMessage
{
    [JsonPropertyOrder(-1)]
    public string Type => "result";

    public JsonElement? Id { get; init; }
    public string? Cmd { get; init; }
    public bool Ok { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    public static ResultMessage Success(Command command) =>
        new() { Id = command.Id, Cmd = command.Name, Ok = true };

    public static ResultMessage Failure(Command? command, string error) =>
        new() { Id = command?.Id, Cmd = string.IsNullOrEmpty(command?.Name) ? null : command.Name, Ok = false, Error = error };
}

internal static class ErrorCodes
{
    public const string NotRunning = "not-running";
    public const string NoSession = "no-session";
    public const string Unsupported = "unsupported";
    public const string Rejected = "rejected";
    public const string Timeout = "timeout";
    public const string Failed = "failed";
    public const string NoWindow = "no-window";
    public const string ForegroundDenied = "foreground-denied";
    public const string UnknownCommand = "unknown-command";
    public const string InvalidJson = "invalid-json";
    public const string MissingCommand = "missing-cmd";
}

internal sealed record Command(JsonElement? Id, string Name);

internal static class Commands
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string TogglePlayPause = "togglePlayPause";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string ToggleShuffle = "toggleShuffle";
    public const string CycleRepeat = "cycleRepeat";
    public const string Focus = "focus";
    public const string Refresh = "refresh";
    public const string Ping = "ping";

    public static readonly IReadOnlyList<string> All =
    [
        Play, Pause, TogglePlayPause, Next, Previous, ToggleShuffle, CycleRepeat, Focus, Refresh, Ping,
    ];

    /// <summary>Returns the canonical spelling, matching case-insensitively (handy when typing by hand).</summary>
    public static string? Normalize(string name)
    {
        foreach (var command in All)
        {
            if (string.Equals(command, name, StringComparison.OrdinalIgnoreCase))
            {
                return command;
            }
        }

        return null;
    }
}

internal static class CommandParser
{
    /// <summary>
    /// Parses one input line: either JSON (<c>{"id":1,"cmd":"next"}</c>) or a bare command name (<c>next</c>).
    /// On failure <paramref name="command"/> still carries whatever id/name could be read, so the error
    /// result can echo them.
    /// </summary>
    public static bool TryParse(string line, out Command? command, out string? error)
    {
        command = null;
        error = null;

        var text = line.Trim();
        if (text.Length == 0)
        {
            error = ErrorCodes.MissingCommand;
            return false;
        }

        JsonElement? id = null;
        string name;

        if (text[0] == '{')
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;

                if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null)
                {
                    // Clone: the element must outlive the JsonDocument.
                    id = idElement.Clone();
                }

                if (!root.TryGetProperty("cmd", out var cmdElement) || cmdElement.ValueKind != JsonValueKind.String)
                {
                    command = new Command(id, "");
                    error = ErrorCodes.MissingCommand;
                    return false;
                }

                name = cmdElement.GetString() ?? "";
            }
            catch (JsonException)
            {
                error = ErrorCodes.InvalidJson;
                return false;
            }
        }
        else
        {
            name = text;
        }

        var normalized = Commands.Normalize(name);
        if (normalized is null)
        {
            command = new Command(id, name);
            error = ErrorCodes.UnknownCommand;
            return false;
        }

        command = new Command(id, normalized);
        return true;
    }
}

/// <summary>Thread-safe writer of protocol messages to stdout (one JSON object per line).</summary>
internal sealed class ProtocolWriter
{
    private readonly Stream _stream;
    private readonly Action _onClosed;
    private readonly object _gate = new();

    /// <param name="stream">Usually <c>Console.OpenStandardOutput()</c>.</param>
    /// <param name="onClosed">Called when the reader has gone away (broken pipe).</param>
    public ProtocolWriter(Stream stream, Action onClosed)
    {
        _stream = stream;
        _onClosed = onClosed;
    }

    public void Send(object message)
    {
        var bytes = ProtocolJson.SerializeToUtf8Bytes(message);
        lock (_gate)
        {
            try
            {
                _stream.Write(bytes);
                _stream.WriteByte((byte)'\n');
                _stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _onClosed();
            }
        }
    }
}
