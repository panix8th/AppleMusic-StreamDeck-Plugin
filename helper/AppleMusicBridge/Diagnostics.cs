using System.Diagnostics;
using Windows.Media.Control;

namespace AppleMusicBridge;

/// <summary>
/// Human-readable listings used by --list-sessions and --debug, to find out the exact
/// SourceAppUserModelId and process name Apple Music uses on a given machine.
/// </summary>
internal static class Diagnostics
{
    private static readonly TimeSpan PropertiesTimeout = TimeSpan.FromSeconds(2);

    /// <summary>--list-sessions: print everything to stdout and exit.</summary>
    public static async Task<int> PrintAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));

        Console.WriteLine("Media sessions (SourceAppUserModelId):");
        foreach (var line in await DescribeSessionsAsync(manager))
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine();
        Console.WriteLine("Processes with \"apple\" or \"music\" in their name:");
        foreach (var line in DescribeProcesses())
        {
            Console.WriteLine("  " + line);
        }

        return 0;
    }

    public static async Task<IReadOnlyList<string>> DescribeSessionsAsync(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try
        {
            sessions = manager.GetSessions();
        }
        catch (Exception ex)
        {
            return [$"(GetSessions failed: {ex.Message})"];
        }

        if (sessions.Count == 0)
        {
            return ["(no media sessions - start playback in an app first)"];
        }

        var lines = new List<string>();
        foreach (var session in sessions)
        {
            var id = Try(() => session.SourceAppUserModelId) ?? "?";
            var status = Try(() => session.GetPlaybackInfo().PlaybackStatus.ToString()) ?? "?";
            string nowPlaying;
            try
            {
                var properties = await session.TryGetMediaPropertiesAsync().AsTask().WaitAsync(PropertiesTimeout);
                nowPlaying = properties is null ? "-" : $"\"{properties.Title}\" / \"{properties.Artist}\"";
            }
            catch (Exception ex)
            {
                nowPlaying = $"(unavailable: {ex.GetType().Name})";
            }

            var marker = AppleMusicIdentity.IsAppleMusicSession(id) ? "   <== matched as Apple Music" : "";
            lines.Add($"\"{id}\"  status={status}  now playing={nowPlaying}{marker}");
        }

        return lines;
    }

    public static IReadOnlyList<string> DescribeProcesses()
    {
        var lines = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var name = Try(() => process.ProcessName);
                if (name is null
                    || (!name.Contains("apple", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("music", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var appId = ProcessInfo.TryGetAppUserModelId((uint)process.Id) ?? "-";
                var title = Try(() => process.MainWindowTitle) ?? "";
                var matched = AppleMusicIdentity.ProcessNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? "   <== matched as Apple Music"
                    : "";
                lines.Add($"{name}.exe  pid={process.Id}  appUserModelId={appId}  mainWindow=\"{title}\"{matched}");
            }
        }

        if (lines.Count == 0)
        {
            lines.Add("(none)");
        }

        return lines;
    }

    private static T? Try<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return default;
        }
    }
}
