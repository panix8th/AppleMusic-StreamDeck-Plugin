using System.Globalization;

namespace AppleMusicBridge;

/// <summary>Command-line options. Platform-neutral: also compiled into the unit tests.</summary>
internal sealed record Options(bool Debug, bool ListSessions, bool Help, int? ParentPid, bool DumpUi = false)
{
    public const string Usage = """
        Usage: AppleMusicBridge.exe [options]

          --debug            Verbose logs on stderr: every media session's SourceAppUserModelId,
                             Apple/Music processes, raw media and playback fields, window search.
                             Also saves the current cover to %TEMP%\AppleMusicBridge\cover.png.
          --list-sessions    Print all media sessions and Apple/Music processes, then exit.
          --dump-ui          Print the controls UI Automation sees in Apple Music's player bar (used for
                             shuffle/repeat) and the state read from them, then exit.
          --parent-pid <id>  Exit as soon as that process exits (used by the Stream Deck plugin).
          --help             Show this help.

        Output: newline-delimited JSON on stdout (hello, state, result); logs on stderr.
        Input:  one command per line on stdin, as JSON  {"id":1,"cmd":"next"}  or just the name  next
        Commands: play, pause, togglePlayPause, next, previous, toggleShuffle, cycleRepeat,
                  focus (bring the Apple Music window to the front), refresh (resend full state), ping
        The helper exits when stdin is closed (Ctrl+Z then Enter in a terminal).
        """;

    public static bool TryParse(string[] args, out Options options, out string? error)
    {
        var debug = false;
        var listSessions = false;
        var dumpUi = false;
        var help = false;
        int? parentPid = null;
        error = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--debug":
                    debug = true;
                    break;
                case "--list-sessions":
                    listSessions = true;
                    break;
                case "--dump-ui":
                    dumpUi = true;
                    break;
                case "--help" or "-h" or "/?":
                    help = true;
                    break;
                case "--parent-pid":
                    if (i + 1 >= args.Length
                        || !int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                        || pid <= 0)
                    {
                        error = "--parent-pid needs a process id.";
                        options = new Options(false, false, false, null);
                        return false;
                    }

                    parentPid = pid;
                    i++;
                    break;
                default:
                    error = $"Unknown argument: {args[i]}";
                    options = new Options(false, false, false, null);
                    return false;
            }
        }

        options = new Options(debug, listSessions, help, parentPid, dumpUi);
        return true;
    }
}
