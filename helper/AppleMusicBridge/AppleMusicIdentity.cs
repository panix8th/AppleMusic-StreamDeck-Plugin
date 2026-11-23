using System.Diagnostics;

namespace AppleMusicBridge;

/// <summary>
/// Decides which media session and which processes are "Apple Music" (the Microsoft Store app).
/// Everything else (Spotify, browsers, iTunes, ...) is ignored.
/// </summary>
internal static class AppleMusicIdentity
{
    /// <summary>
    /// Matched case-insensitively against each session's SourceAppUserModelId.
    /// TODO(step 2): replace with an exact match on the ID that --debug reports on the target machine.
    /// </summary>
    public const string SessionIdFragment = "AppleMusic";

    /// <summary>
    /// Process image names (without ".exe") of the Apple Music app.
    /// TODO(step 2): confirm with --debug / --list-sessions on the target machine.
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames = ["AppleMusic"];

    public static bool IsAppleMusicSession(string? sourceAppUserModelId) =>
        !string.IsNullOrEmpty(sourceAppUserModelId)
        && sourceAppUserModelId.Contains(SessionIdFragment, StringComparison.OrdinalIgnoreCase);

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
