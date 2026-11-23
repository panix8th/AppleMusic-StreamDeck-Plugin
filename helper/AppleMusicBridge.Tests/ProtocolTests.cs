using System.Text;
using System.Text.Json;
using AppleMusicBridge;
using Xunit;

namespace AppleMusicBridge.Tests;

public class CommandParserTests
{
    [Fact]
    public void Parses_json_command_with_numeric_id()
    {
        Assert.True(CommandParser.TryParse("""{"id":1,"cmd":"next"}""", out var command, out var error));
        Assert.Null(error);
        Assert.Equal(Commands.Next, command!.Name);
        Assert.Equal(1, command.Id!.Value.GetInt32());
    }

    [Fact]
    public void Normalizes_command_case_and_keeps_string_id()
    {
        Assert.True(CommandParser.TryParse("""{"id":"abc","cmd":"TOGGLEPLAYPAUSE"}""", out var command, out _));
        Assert.Equal(Commands.TogglePlayPause, command!.Name);
        Assert.Equal("abc", command.Id!.Value.GetString());
    }

    [Theory]
    [InlineData("next", Commands.Next)]
    [InlineData("  Focus  ", Commands.Focus)]
    [InlineData("cyclerepeat", Commands.CycleRepeat)]
    [InlineData("toggleShuffle", Commands.ToggleShuffle)]
    public void Accepts_bare_command_names_typed_in_a_terminal(string line, string expected)
    {
        Assert.True(CommandParser.TryParse(line, out var command, out _));
        Assert.Equal(expected, command!.Name);
        Assert.Null(command.Id);
    }

    [Fact]
    public void Every_documented_command_is_accepted()
    {
        foreach (var name in Commands.All)
        {
            Assert.True(CommandParser.TryParse($$"""{"id":0,"cmd":"{{name}}"}""", out var command, out _), name);
            Assert.Equal(name, command!.Name);
        }
    }

    [Fact]
    public void Missing_cmd_keeps_the_id_for_the_error_result()
    {
        Assert.False(CommandParser.TryParse("""{"id":2}""", out var command, out var error));
        Assert.Equal(ErrorCodes.MissingCommand, error);
        Assert.Equal(2, command!.Id!.Value.GetInt32());
    }

    [Fact]
    public void Non_string_cmd_is_rejected()
    {
        Assert.False(CommandParser.TryParse("""{"id":2,"cmd":5}""", out _, out var error));
        Assert.Equal(ErrorCodes.MissingCommand, error);
    }

    [Fact]
    public void Unknown_command_is_rejected_with_id_and_name()
    {
        Assert.False(CommandParser.TryParse("""{"id":3,"cmd":"dance"}""", out var command, out var error));
        Assert.Equal(ErrorCodes.UnknownCommand, error);
        Assert.Equal("dance", command!.Name);
        Assert.Equal(3, command.Id!.Value.GetInt32());
    }

    [Fact]
    public void Broken_json_is_rejected()
    {
        Assert.False(CommandParser.TryParse("{not json", out var command, out var error));
        Assert.Equal(ErrorCodes.InvalidJson, error);
        Assert.Null(command);
    }

    [Fact]
    public void Id_survives_after_parsing_finished()
    {
        // The id is cloned out of the JsonDocument, which is disposed inside TryParse.
        CommandParser.TryParse("""{"id":{"nested":[1,2]},"cmd":"ping"}""", out var command, out _);
        var json = ProtocolJson.Serialize(ResultMessage.Success(command!));
        Assert.Equal("""{"type":"result","id":{"nested":[1,2]},"cmd":"ping","ok":true}""", json);
    }
}

public class MessageSerializationTests
{
    [Fact]
    public void Hello_message_shape()
    {
        var json = ProtocolJson.Serialize(new HelloMessage("0.1.0", 42, false));
        Assert.Equal("""{"type":"hello","version":"0.1.0","pid":42,"debug":false}""", json);
    }

    [Fact]
    public void Success_result_echoes_id_and_omits_error()
    {
        CommandParser.TryParse("""{"id":7,"cmd":"next"}""", out var command, out _);
        Assert.Equal(
            """{"type":"result","id":7,"cmd":"next","ok":true}""",
            ProtocolJson.Serialize(ResultMessage.Success(command!)));
    }

    [Fact]
    public void Failure_result_carries_error_code()
    {
        CommandParser.TryParse("""{"id":"x","cmd":"play"}""", out var command, out _);
        Assert.Equal(
            """{"type":"result","id":"x","cmd":"play","ok":false,"error":"not-running"}""",
            ProtocolJson.Serialize(ResultMessage.Failure(command, ErrorCodes.NotRunning)));
    }

    [Fact]
    public void Failure_without_command_has_null_id_and_cmd()
    {
        Assert.Equal(
            """{"type":"result","id":null,"cmd":null,"ok":false,"error":"invalid-json"}""",
            ProtocolJson.Serialize(ResultMessage.Failure(null, ErrorCodes.InvalidJson)));
    }

    [Fact]
    public void State_without_art_omits_albumArt_but_keeps_albumArtId()
    {
        var state = SampleState() with { AlbumArtId = null, AlbumArt = null };
        using var document = JsonDocument.Parse(ProtocolJson.Serialize(state));
        var root = document.RootElement;

        Assert.Equal("state", root.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("albumArtId").ValueKind);
        Assert.False(root.TryGetProperty("albumArt", out _));
    }

    [Fact]
    public void State_message_has_the_documented_fields_in_camelCase_with_type_first()
    {
        var json = ProtocolJson.Serialize(SampleState() with { AlbumArt = "iVBORw0KGgo=" });
        Assert.StartsWith("""{"type":"state",""", json);

        using var document = JsonDocument.Parse(json);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(
            new[]
            {
                "type", "appRunning", "hasSession", "title", "artist", "album", "isPlaying", "status", "shuffle",
                "repeat", "position", "duration", "timestamp", "albumArtId", "albumArt", "supports", "sourceAppId",
            },
            names);

        var supports = document.RootElement.GetProperty("supports").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "play", "pause", "playPause", "next", "previous", "shuffle", "repeat" }, supports);
        Assert.Equal("all", document.RootElement.GetProperty("repeat").GetString());
        Assert.Equal(63.2, document.RootElement.GetProperty("position").GetDouble());
    }

    [Fact]
    public void Output_is_one_ascii_line_even_for_non_ascii_titles()
    {
        var state = SampleState() with { Title = "Für Elise — 東京\nline2 \"quoted\" \U0001F3B5" };
        var bytes = ProtocolJson.SerializeToUtf8Bytes(state);

        Assert.All(bytes, b => Assert.True(b < 0x80, "non-ASCII byte in output"));
        Assert.DoesNotContain((byte)'\n', bytes);

        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(state.Title, document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void Writer_appends_a_newline_per_message()
    {
        using var stream = new MemoryStream();
        var writer = new ProtocolWriter(stream, () => throw new InvalidOperationException("should stay open"));
        writer.Send(new HelloMessage("1.0.0", 1, true));
        writer.Send(ResultMessage.Failure(null, ErrorCodes.Timeout));

        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("", lines[2]);
        Assert.StartsWith("""{"type":"hello",""", lines[0]);
        Assert.StartsWith("""{"type":"result",""", lines[1]);
    }

    [Fact]
    public void Writer_reports_a_closed_pipe_instead_of_throwing()
    {
        var stream = new MemoryStream();
        stream.Dispose();
        var closed = false;
        var writer = new ProtocolWriter(stream, () => closed = true);

        writer.Send(new HelloMessage("1.0.0", 1, false));
        Assert.True(closed);
    }

    internal static StateMessage SampleState() => new()
    {
        AppRunning = true,
        HasSession = true,
        Title = "Song",
        Artist = "Artist",
        Album = "Album",
        IsPlaying = true,
        Status = "playing",
        Shuffle = false,
        Repeat = "all",
        Position = 63.2,
        Duration = 241,
        Timestamp = 1_700_000_000_000,
        AlbumArtId = "0123456789abcdef",
        Supports = new Capabilities(true, true, true, true, true, false, false),
        SourceAppId = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App",
    };
}
