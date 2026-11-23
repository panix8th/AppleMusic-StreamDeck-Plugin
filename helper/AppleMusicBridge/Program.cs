using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace AppleMusicBridge;

internal static class Program
{
    private static readonly string Version =
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // Kept in a field so its Exited event keeps working.
    private static Process? _parent;

    public static async Task<int> Main(string[] args)
    {
        if (!Options.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        if (options.Help)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        Log.Initialize(options.Debug);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Unhandled exception: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warn($"Unobserved task exception: {e.Exception.GetBaseException()}");
            e.SetObserved();
        };

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            Log.Error("Windows 10 version 1809 or newer is required.");
            return 3;
        }

        if (options.ListSessions)
        {
            return await Diagnostics.PrintAsync();
        }

        var output = new ProtocolWriter(Console.OpenStandardOutput(), () => Exit("stdout was closed; exiting.", 0));
        Log.Info($"AppleMusicBridge {Version} started (pid {Environment.ProcessId}{(options.Debug ? ", debug mode" : "")}).");
        output.Send(new HelloMessage(Version, Environment.ProcessId, options.Debug));

        if (options.ParentPid is { } parentPid)
        {
            WatchParent(parentPid);
        }

        using var bridge = new MediaBridge(output, options.Debug, abbreviateArt: !Console.IsOutputRedirected);
        try
        {
            await bridge.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Cannot access Windows media sessions: {ex}");
            return 3;
        }

        await ReadCommandsAsync(bridge, output);
        Log.Info("stdin was closed; exiting.");
        return 0;
    }

    /// <summary>
    /// Reads commands until stdin is closed. When the plugin process ends (even if it is killed), its end of
    /// the pipe closes and this returns, so the helper never outlives the plugin.
    /// </summary>
    private static async Task ReadCommandsAsync(MediaBridge bridge, ProtocolWriter output)
    {
        // Commands run one at a time, in order, without blocking the reader.
        var queue = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var worker = Task.Run(async () =>
        {
            await foreach (var command in queue.Reader.ReadAllAsync())
            {
                output.Send(await bridge.ExecuteAsync(command));
            }
        });

        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (CommandParser.TryParse(line, out var command, out var error))
            {
                Log.Debug($"Command received: {command!.Name}");
                queue.Writer.TryWrite(command);
            }
            else
            {
                Log.Warn($"Ignoring input ({error}): {(line.Length > 200 ? line[..200] + "..." : line)}");
                output.Send(ResultMessage.Failure(command, error ?? ErrorCodes.InvalidJson));
            }
        }

        queue.Writer.TryComplete();
        await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(2)));
    }

    /// <summary>Second safety net next to stdin: exit as soon as the parent (the plugin) exits.</summary>
    private static void WatchParent(int processId)
    {
        try
        {
            _parent = Process.GetProcessById(processId);
            _parent.EnableRaisingEvents = true;
            _parent.Exited += (_, _) => Exit($"Parent process {processId} exited; exiting.", 0);
            if (_parent.HasExited)
            {
                Exit($"Parent process {processId} already exited; exiting.", 0);
            }

            Log.Debug($"Watching parent process {processId}.");
        }
        catch (ArgumentException)
        {
            Exit($"Parent process {processId} is not running; exiting.", 0);
        }
        catch (Exception ex)
        {
            Log.Warn($"Cannot watch parent process {processId}: {ex.Message}");
        }
    }

    private static void Exit(string reason, int exitCode)
    {
        Log.Info(reason);
        Environment.Exit(exitCode);
    }
}
