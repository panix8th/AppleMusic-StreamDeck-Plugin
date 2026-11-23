using System.Globalization;
using System.Text;

namespace AppleMusicBridge;

/// <summary>
/// Logs to stderr, one line per entry: <c>[LEVEL] message</c>. The Stream Deck plugin forwards these
/// lines into its own log and maps LEVEL to its log levels. In an interactive terminal a time stamp is
/// prepended. stdout is reserved for the protocol, so nothing else may write to it.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static TextWriter _writer = TextWriter.Null;
    private static bool _timestamps;

    public static bool IsDebugEnabled { get; private set; }

    public static void Initialize(bool debug)
    {
        IsDebugEnabled = debug;

        if (Console.IsErrorRedirected)
        {
            // Spawned by the plugin: plain UTF-8 lines; the plugin's logger adds its own time stamps.
            _writer = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            _timestamps = false;
        }
        else
        {
            // Interactive terminal: switch the console to UTF-8 so titles with accents/CJK display correctly.
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (Exception)
            {
                // Cosmetic only.
            }

            _writer = Console.Error;
            _timestamps = true;
        }
    }

    public static void Debug(string message)
    {
        if (IsDebugEnabled)
        {
            Write("DEBUG", message);
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        // One entry per line, so the plugin can map every line to a level.
        var text = message.ReplaceLineEndings(" | ");
        var line = _timestamps
            ? $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {text}\n"
            : $"[{level}] {text}\n";

        lock (Gate)
        {
            try
            {
                _writer.Write(line);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // stderr is gone; nothing sensible left to do.
            }
        }
    }
}
