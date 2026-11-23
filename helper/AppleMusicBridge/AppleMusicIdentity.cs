using System.Diagnostics;

namespace AppleMusicBridge;

/// <summary>
/// Decides which media session and which processes are "Apple Music" (the Microsoft Store app).
/// Everything else (Spotify, browsers, iTunes, ...) is ignored.
/// </summary>
internal static class AppleMusicIdentity
{
    /// <summary>
    /// SourceAppUserModelId of the Apple Music media session, as reported by the app on Windows
    /// (package family name "AppleInc.AppleMusicWin_nzyj5cx40ttqa" + application id "App").
    /// Confirmed with --debug on a real machine.
    /// </summary>
    public const string SessionId = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";

    /// <summary>
    /// Process image names (without ".exe") of the Apple Music app. Only used as a fallback: when Apple
    /// Music has no media session yet (running, nothing loaded) and to find its window.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["AppleMusic"];

    /// <summary>Exact, case-insensitive match; every other app's session is ignored.</summary>
    public static bool IsAppleMusicSession(string? sourceAppUserModelId) =>
        string.Equals(sourceAppUserModelId, SessionId, StringComparison.OrdinalIgnoreCase);

    public static bool IsAppleMusicProcessRunning() => GetProcessIds().Count > 0;

    public static HashSet<uint> GetProcessIds()
    {
        var ids = new HashSet<uint>();
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    ids.Add((uint)process.Id);
                }
            }
        }

        return ids;
    }
}
