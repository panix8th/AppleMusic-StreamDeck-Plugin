using System.Runtime.InteropServices;

namespace AppleMusicBridge;

internal sealed record ActivationResult(bool Ok, string? Error, string Detail);

internal sealed record WindowInfo(
    IntPtr Handle,
    uint ProcessId,
    string ClassName,
    string Title,
    bool Minimized,
    bool Cloaked,
    bool ToolWindow,
    bool Owned)
{
    /// <summary>A normal top-level app window (not a tool window, popup or untitled helper window).</summary>
    public bool IsMainWindowCandidate => !ToolWindow && !Owned && Title.Length > 0;

    public override string ToString() =>
        $"hwnd=0x{Handle.ToInt64():X} pid={ProcessId} class=\"{ClassName}\" title=\"{Title}\" " +
        $"minimized={Minimized} cloaked={Cloaked} toolWindow={ToolWindow} owned={Owned}";
}

/// <summary>
/// Brings an already open Apple Music window to the front. Never starts Apple Music: when no window
/// exists, it only reports failure.
/// </summary>
internal static class WindowActivator
{
    // Classic UWP apps are hosted inside an ApplicationFrameHost.exe window of this class.
    private const string UwpFrameClass = "ApplicationFrameWindow";

    public static ActivationResult BringToFront(string? sessionAppId)
    {
        var target = FindMainWindow(sessionAppId, out var processIds);
        if (target is null)
        {
            return new ActivationResult(
                false,
                ErrorCodes.NoWindow,
                processIds.Count == 0 ? "Apple Music is not running." : "Apple Music has no visible window.");
        }

        return Activate(target);
    }

    /// <summary>Apple Music's main window, or null when it has none (or is not running).</summary>
    public static WindowInfo? FindMainWindow(string? sessionAppId) => FindMainWindow(sessionAppId, out _);

    private static WindowInfo? FindMainWindow(string? sessionAppId, out HashSet<uint> processIds)
    {
        processIds = AppleMusicIdentity.GetProcessIds();
        var windows = FindWindows(processIds, sessionAppId);

        if (Log.IsDebugEnabled)
        {
            Log.Debug($"Window search: Apple Music pids=[{string.Join(", ", processIds)}], session id=\"{sessionAppId}\", {windows.Count} candidate(s).");
            foreach (var window in windows)
            {
                Log.Debug($"  {window}");
            }
        }

        // Prefer a real main window that is not cloaked (cloaked = hidden by DWM, e.g. on another virtual desktop).
        return windows.FirstOrDefault(w => w.IsMainWindowCandidate && (!w.Cloaked || w.Minimized))
            ?? windows.FirstOrDefault(w => w.IsMainWindowCandidate)
            ?? windows.FirstOrDefault();
    }

    private static List<WindowInfo> FindWindows(HashSet<uint> processIds, string? sessionAppId)
    {
        var appIdCache = new Dictionary<uint, string?>();

        bool BelongsToAppleMusic(uint processId)
        {
            if (processIds.Contains(processId))
            {
                return true;
            }

            if (string.IsNullOrEmpty(sessionAppId))
            {
                return false;
            }

            // Fallback when the exe name differs: compare the process's package identity with the session's.
            if (!appIdCache.TryGetValue(processId, out var appId))
            {
                appId = ProcessInfo.TryGetAppUserModelId(processId);
                appIdCache[processId] = appId;
            }

            return appId is not null && string.Equals(appId, sessionAppId, StringComparison.OrdinalIgnoreCase);
        }

        var result = new List<WindowInfo>();
        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return true;
            }

            if (NativeMethods.GetWindowThreadProcessId(hwnd, out var processId) == 0)
            {
                return true;
            }

            var className = GetClassName(hwnd);
            var matches = BelongsToAppleMusic(processId)
                || (className == UwpFrameClass && HostsChildOf(hwnd, BelongsToAppleMusic));

            if (matches)
            {
                result.Add(Describe(hwnd, processId, className));
            }

            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    private static bool HostsChildOf(IntPtr frame, Func<uint, bool> belongsToAppleMusic)
    {
        var found = false;
        NativeMethods.EnumWindowsProc callback = (child, _) =>
        {
            if (NativeMethods.GetWindowThreadProcessId(child, out var processId) != 0 && belongsToAppleMusic(processId))
            {
                found = true;
                return false;
            }

            return true;
        };

        NativeMethods.EnumChildWindows(frame, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    private static ActivationResult Activate(WindowInfo window)
    {
        var hwnd = window.Handle;

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }
        else if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            return Success(window, "already in front");
        }

        if (TrySetForeground(hwnd))
        {
            return Success(window, "SetForegroundWindow");
        }

        // Windows only lets the process that received the last input event change the foreground window.
        // Injecting a no-op mouse input makes this process that one (the same trick PowerToys uses).
        SendNoOpInput();
        if (TrySetForeground(hwnd))
        {
            return Success(window, "SetForegroundWindow after no-op input");
        }

        if (TryWithAttachedInput(hwnd))
        {
            return Success(window, "AttachThreadInput");
        }

        NativeMethods.SwitchToThisWindow(hwnd, true);
        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            return Success(window, "SwitchToThisWindow");
        }

        return new ActivationResult(
            false,
            ErrorCodes.ForegroundDenied,
            $"Windows did not allow bringing the window to the front ({window}).");
    }

    private static ActivationResult Success(WindowInfo window, string how) =>
        new(true, null, $"{how}: {window}");

    private static bool TrySetForeground(IntPtr hwnd)
    {
        NativeMethods.SetForegroundWindow(hwnd);
        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    private static void SendNoOpInput()
    {
        var inputs = new[] { new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE } };
        if (NativeMethods.SendInput(1, inputs, Marshal.SizeOf<NativeMethods.INPUT>()) == 0)
        {
            Log.Debug($"SendInput failed (Win32 error {Marshal.GetLastPInvokeError()}).");
        }
    }

    private static bool TryWithAttachedInput(IntPtr hwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return false;
        }

        if (!NativeMethods.AttachThreadInput(currentThread, foregroundThread, true))
        {
            return false;
        }

        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }

        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    private static WindowInfo Describe(IntPtr hwnd, uint processId, string className)
    {
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var cloaked = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloakedValue, sizeof(int)) == 0
            && cloakedValue != 0;

        return new WindowInfo(
            hwnd,
            processId,
            className,
            GetTitle(hwnd),
            NativeMethods.IsIconic(hwnd),
            cloaked,
            (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0,
            NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero);
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return "";
        }

        var buffer = new char[length + 1];
        var copied = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : "";
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var copied = NativeMethods.GetClassName(hwnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : "";
    }
}
