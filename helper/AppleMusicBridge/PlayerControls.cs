using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace AppleMusicBridge;

/// <summary>Shuffle/repeat as shown by Apple Music's player-bar buttons.</summary>
internal sealed record PlayerControlsState(bool HasShuffle, bool? Shuffle, bool HasRepeat, RepeatMode? Repeat)
{
    public static readonly PlayerControlsState None = new(false, null, false, null);
}

internal sealed record ControlActionResult(bool Ok, string? Error);

/// <summary>
/// Apple Music for Windows does not report or accept shuffle and repeat through Windows' media controls. This
/// class reads and presses its own Shuffle and Repeat buttons through UI Automation (the accessibility API that
/// screen readers use): no mouse clicks, and the window is not brought to the front.
/// </summary>
internal sealed class PlayerControls : IDisposable
{
    // Ids from UIAutomationClient.h.
    private const int InvokePatternId = 10000;
    private const int TogglePatternId = 10015;
    private const int LegacyIAccessiblePatternId = 10018;
    private const int NamePropertyId = 30005;
    private const int HelpTextPropertyId = 30013;
    private const int ItemStatusPropertyId = 30026;
    private const int IsInvokePatternAvailablePropertyId = 30031;
    private const int IsTogglePatternAvailablePropertyId = 30041;
    private const int IsLegacyIAccessiblePatternAvailablePropertyId = 30090;
    private const int ToggleStatePropertyId = 30086;
    private const int LegacyIAccessibleValuePropertyId = 30093;
    private const int LegacyIAccessibleStatePropertyId = 30096;

    private const int MaxElements = 5000;
    private static readonly TimeSpan RetryDiscoveryAfter = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly PropertyChangedHandler _handler;
    private IUIAutomation? _automation;
    private Found? _shuffle;
    private Found? _repeat;
    private bool _windowFound;
    private DateTime _lastDiscovery = DateTime.MinValue;
    private string? _lastOutcome;

    private sealed record Found(IUIAutomationElement Element, ControlCandidate Description);

    /// <param name="onChanged">Called (on a UI Automation thread) when one of the buttons changes.</param>
    public PlayerControls(Action onChanged)
    {
        _handler = new PropertyChangedHandler(onChanged);
    }

    /// <summary>Current shuffle/repeat state. Looks for the buttons at most every 20 s while they are missing.</summary>
    public PlayerControlsState Read(string? sessionAppId)
    {
        lock (_gate)
        {
            try
            {
                EnsureDiscovered(sessionAppId, force: false);
                return new PlayerControlsState(
                    _shuffle is not null,
                    _shuffle is null ? null : ReadShuffle(_shuffle),
                    _repeat is not null,
                    _repeat is null ? null : ReadRepeat(_repeat));
            }
            catch (COMException ex)
            {
                Log.Debug($"UI Automation: reading the buttons failed ({ex.Message}); looking for them again next time.");
                Reset();
                _lastDiscovery = DateTime.MinValue;
                return PlayerControlsState.None;
            }
        }
    }

    public ControlActionResult ToggleShuffle(string? sessionAppId) => Press(PlayerControlKind.Shuffle, sessionAppId);

    public ControlActionResult CycleRepeat(string? sessionAppId) => Press(PlayerControlKind.Repeat, sessionAppId);

    public void Dispose()
    {
        lock (_gate)
        {
            Reset();
            try
            {
                _automation?.RemoveAllEventHandlers();
            }
            catch (COMException)
            {
                // Shutting down anyway.
            }
        }
    }

    /// <summary>
    /// --dump-ui: prints what UI Automation shows of Apple Music's player bar (and anything mentioning shuffle or
    /// repeat), which buttons were picked and the state read from them.
    /// </summary>
    public static int Dump(TextWriter output)
    {
        var window = WindowActivator.FindMainWindow(AppleMusicIdentity.SessionId);
        if (window is null)
        {
            output.WriteLine("Apple Music has no window. Start Apple Music (and play something) first.");
            return 1;
        }

        var automation = CreateAutomation();
        var root = automation.ElementFromHandle(window.Handle);
        var windowRect = root.CurrentBoundingRectangle;
        output.WriteLine($"Apple Music window: {window}");
        output.WriteLine($"Window rectangle: ({windowRect.left},{windowRect.top})-({windowRect.right},{windowRect.bottom})");
        output.WriteLine();
        output.WriteLine("Controls in the top 30% of the window that can be pressed, and every control mentioning shuffle/repeat:");

        var all = root.FindAll(TreeScope.TreeScope_Descendants, automation.CreateTrueCondition());
        var count = Math.Min(all.Length, MaxElements);
        var shown = 0;
        for (var i = 0; i < count; i++)
        {
            try
            {
                var element = all.GetElement(i);
                var description = Describe(element, windowRect, window.Minimized);
                var pressable = description.CanInvoke || description.CanToggle;
                var inTopBand = description.RelativeTop is >= 0 and <= 0.3;
                var mentions = PlayerControlMatching.MentionsShuffleOrRepeat(description.Name)
                    || PlayerControlMatching.MentionsShuffleOrRepeat(description.AutomationId)
                    || PlayerControlMatching.MentionsShuffleOrRepeat(description.HelpText);
                if (!(pressable && inTopBand) && !mentions)
                {
                    continue;
                }

                shown++;
                output.WriteLine($"  {DescribeForDump(element, description)}");
            }
            catch (COMException)
            {
                // Element vanished while walking the tree.
            }
        }

        output.WriteLine($"  ({shown} shown, {all.Length} elements in the window)");
        output.WriteLine();

        using var controls = new PlayerControls(() => { });
        var state = controls.Read(AppleMusicIdentity.SessionId);
        output.WriteLine($"Picked shuffle: {controls._shuffle?.Description.ToString() ?? "none"}");
        output.WriteLine($"Picked repeat:  {controls._repeat?.Description.ToString() ?? "none"}");
        output.WriteLine($"State read:     shuffle={Show(state.Shuffle)} repeat={(state.Repeat is { } mode ? RepeatModes.ToWire(mode) : "unknown")}");
        return 0;
    }

    private ControlActionResult Press(PlayerControlKind kind, string? sessionAppId)
    {
        lock (_gate)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    EnsureDiscovered(sessionAppId, force: true);
                    var found = kind == PlayerControlKind.Shuffle ? _shuffle : _repeat;
                    if (found is null)
                    {
                        return new ControlActionResult(false, _windowFound ? ErrorCodes.Unsupported : ErrorCodes.NoWindow);
                    }

                    Activate(found.Element, kind);
                    Log.Debug($"UI Automation: pressed {kind}.");
                    return new ControlActionResult(true, null);
                }
                catch (COMException ex)
                {
                    // The button went away (window recreated); look for it again once.
                    Log.Debug($"UI Automation: pressing {kind} failed ({ex.Message}).");
                    Reset();
                }
                catch (InvalidOperationException ex)
                {
                    Log.Info($"UI Automation: {ex.Message}");
                    return new ControlActionResult(false, ErrorCodes.Unsupported);
                }
            }

            return new ControlActionResult(false, ErrorCodes.Failed);
        }
    }

    private static void Activate(IUIAutomationElement element, PlayerControlKind kind)
    {
        // Shuffle is an on/off toggle; Repeat steps through its modes. Prefer the pattern that fits each.
        if (kind == PlayerControlKind.Shuffle && element.GetCurrentPattern(TogglePatternId) is IUIAutomationTogglePattern toggleFirst)
        {
            toggleFirst.Toggle();
            return;
        }

        if (element.GetCurrentPattern(InvokePatternId) is IUIAutomationInvokePattern invoke)
        {
            invoke.Invoke();
            return;
        }

        if (element.GetCurrentPattern(TogglePatternId) is IUIAutomationTogglePattern toggle)
        {
            toggle.Toggle();
            return;
        }

        if (element.GetCurrentPattern(LegacyIAccessiblePatternId) is IUIAutomationLegacyIAccessiblePattern legacy)
        {
            legacy.DoDefaultAction();
            return;
        }

        throw new InvalidOperationException($"The {kind} button cannot be pressed through UI Automation.");
    }

    private void EnsureDiscovered(string? sessionAppId, bool force)
    {
        if (_shuffle is not null && _repeat is not null && IsAlive(_shuffle) && IsAlive(_repeat))
        {
            return;
        }

        if (!force && DateTime.UtcNow - _lastDiscovery < RetryDiscoveryAfter && (_shuffle is null || IsAlive(_shuffle)) && (_repeat is null || IsAlive(_repeat)))
        {
            return;
        }

        _lastDiscovery = DateTime.UtcNow;
        Discover(sessionAppId);
    }

    private void Discover(string? sessionAppId)
    {
        Reset();
        var window = WindowActivator.FindMainWindow(sessionAppId);
        _windowFound = window is not null;
        if (window is null)
        {
            Report("UI Automation: Apple Music has no window, so shuffle/repeat cannot be reached.");
            return;
        }

        var automation = Automation;
        var root = automation.ElementFromHandle(window.Handle);
        var windowRect = root.CurrentBoundingRectangle;

        // Everything that can be pressed or toggled; the matching decides which ones are Shuffle and Repeat.
        var pressable = automation.CreateOrCondition(
            automation.CreatePropertyCondition(IsInvokePatternAvailablePropertyId, true),
            automation.CreatePropertyCondition(IsTogglePatternAvailablePropertyId, true));
        var elements = root.FindAll(TreeScope.TreeScope_Descendants, pressable);

        var candidates = new List<Found>();
        var count = Math.Min(elements.Length, MaxElements);
        for (var i = 0; i < count; i++)
        {
            try
            {
                var element = elements.GetElement(i);
                // Cheap first pass: only elements whose id, name or help text mention shuffle/repeat.
                var name = element.CurrentName ?? "";
                var automationId = element.CurrentAutomationId ?? "";
                if (!PlayerControlMatching.MentionsShuffleOrRepeat(name)
                    && !PlayerControlMatching.MentionsShuffleOrRepeat(automationId)
                    && !PlayerControlMatching.MentionsShuffleOrRepeat(element.CurrentHelpText))
                {
                    continue;
                }

                candidates.Add(new Found(element, Describe(element, windowRect, window.Minimized)));
            }
            catch (COMException)
            {
                // Element vanished while scanning.
            }
        }

        _shuffle = Pick(PlayerControlKind.Shuffle, candidates);
        _repeat = Pick(PlayerControlKind.Repeat, candidates);

        foreach (var candidate in candidates)
        {
            Log.Debug($"UI Automation candidate: {candidate.Description}");
        }

        Subscribe(_shuffle);
        Subscribe(_repeat);

        Report(
            $"UI Automation: {count} pressable controls in Apple Music's window; " +
            $"shuffle button {(_shuffle is null ? "NOT found" : $"found ({Summary(_shuffle.Description)})")}, " +
            $"repeat button {(_repeat is null ? "NOT found" : $"found ({Summary(_repeat.Description)})")}." +
            (_shuffle is null || _repeat is null ? " Run AppleMusicBridge.exe --dump-ui to see what Apple Music offers." : ""));
    }

    private static Found? Pick(PlayerControlKind kind, List<Found> candidates)
    {
        var best = PlayerControlMatching.Choose(kind, candidates.Select(candidate => candidate.Description));
        return best is null ? null : candidates.First(candidate => ReferenceEquals(candidate.Description, best));
    }

    private void Subscribe(Found? found)
    {
        if (found is null)
        {
            return;
        }

        try
        {
            Automation.AddPropertyChangedEventHandler(
                found.Element,
                TreeScope.TreeScope_Element,
                null,
                _handler,
                [ToggleStatePropertyId, NamePropertyId, HelpTextPropertyId, ItemStatusPropertyId, LegacyIAccessibleStatePropertyId, LegacyIAccessibleValuePropertyId]);
        }
        catch (COMException ex)
        {
            // Not fatal: the state is still read on every other update.
            Log.Debug($"UI Automation: cannot watch {found.Description.Name} for changes ({ex.Message}).");
        }
    }

    private void Reset()
    {
        foreach (var found in new[] { _shuffle, _repeat })
        {
            if (found is null || _automation is null)
            {
                continue;
            }

            try
            {
                _automation.RemovePropertyChangedEventHandler(found.Element, _handler);
            }
            catch (COMException)
            {
                // Already gone.
            }
        }

        _shuffle = null;
        _repeat = null;
    }

    /// <summary>Logs at INFO when the outcome changes, otherwise at DEBUG (discovery repeats every 20 s while incomplete).</summary>
    private void Report(string outcome)
    {
        if (outcome == _lastOutcome)
        {
            Log.Debug(outcome);
            return;
        }

        _lastOutcome = outcome;
        Log.Info(outcome);
    }

    private static bool? ReadShuffle(Found found)
    {
        var element = found.Element;
        var (legacyState, legacyValue) = found.Description.CanToggle ? (null, null) : ReadLegacy(element);
        return PlayerControlMatching.ParseShuffle(
            ReadToggle(element),
            legacyState,
            element.CurrentItemStatus,
            legacyValue,
            element.CurrentName,
            element.CurrentHelpText);
    }

    private static RepeatMode? ReadRepeat(Found found)
    {
        var element = found.Element;
        var (legacyState, legacyValue) = found.Description.CanToggle ? (null, null) : ReadLegacy(element);
        return PlayerControlMatching.ParseRepeat(
            ReadToggle(element),
            legacyState,
            element.CurrentItemStatus,
            legacyValue,
            element.CurrentName,
            element.CurrentHelpText);
    }

    private static int? ReadToggle(IUIAutomationElement element) =>
        element.GetCurrentPattern(TogglePatternId) is IUIAutomationTogglePattern toggle ? (int)toggle.CurrentToggleState : null;

    private static (uint? State, string? Value) ReadLegacy(IUIAutomationElement element) =>
        element.GetCurrentPattern(LegacyIAccessiblePatternId) is IUIAutomationLegacyIAccessiblePattern legacy
            ? (legacy.CurrentState, legacy.CurrentValue)
            : (null, null);

    private static bool IsAlive(Found found)
    {
        try
        {
            _ = found.Element.CurrentControlType;
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static ControlCandidate Describe(IUIAutomationElement element, tagRECT window, bool windowMinimized)
    {
        var rect = element.CurrentBoundingRectangle;
        double? relativeTop = null;
        var windowHeight = window.bottom - window.top;
        if (!windowMinimized && windowHeight > 100 && rect.bottom > rect.top)
        {
            relativeTop = (double)(rect.top - window.top) / windowHeight;
        }

        return new ControlCandidate(
            element.CurrentName ?? "",
            element.CurrentAutomationId ?? "",
            element.CurrentHelpText ?? "",
            element.CurrentClassName ?? "",
            element.GetCurrentPropertyValue(IsTogglePatternAvailablePropertyId) is true,
            element.GetCurrentPropertyValue(IsInvokePatternAvailablePropertyId) is true,
            element.GetCurrentPropertyValue(IsLegacyIAccessiblePatternAvailablePropertyId) is true,
            relativeTop);
    }

    private static string DescribeForDump(IUIAutomationElement element, ControlCandidate description)
    {
        var toggle = ReadToggle(element);
        var (legacyState, legacyValue) = ReadLegacy(element);
        var rect = element.CurrentBoundingRectangle;
        return $"{element.CurrentLocalizedControlType} name=\"{description.Name}\" id=\"{description.AutomationId}\" " +
            $"class=\"{description.ClassName}\" help=\"{description.HelpText}\" status=\"{element.CurrentItemStatus}\" " +
            $"invoke={description.CanInvoke} toggle={(toggle is null ? "-" : ((ToggleState)toggle.Value).ToString())} " +
            $"legacyState={(legacyState is null ? "-" : $"0x{legacyState:X}")} legacyValue=\"{legacyValue}\" " +
            $"rect=({rect.left},{rect.top},{rect.right - rect.left}x{rect.bottom - rect.top}) " +
            $"top={(description.RelativeTop is { } top ? top.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "?")} " +
            $"shuffleScore={PlayerControlMatching.Score(PlayerControlKind.Shuffle, description)} " +
            $"repeatScore={PlayerControlMatching.Score(PlayerControlKind.Repeat, description)}";
    }

    private static string Summary(ControlCandidate candidate) =>
        $"name \"{candidate.Name}\", id \"{candidate.AutomationId}\", {(candidate.CanToggle ? "toggle" : candidate.CanInvoke ? "button" : "default action")}";

    private static string Show(bool? value) => value is null ? "unknown" : value.Value ? "on" : "off";

    private IUIAutomation Automation => _automation ??= CreateAutomation();

    private static IUIAutomation CreateAutomation()
    {
        IUIAutomation2 automation = new CUIAutomation8();
        // Never let a busy or hung Apple Music block the helper for long.
        automation.ConnectionTimeout = 2_000;
        automation.TransactionTimeout = 3_000;
        return automation;
    }
}

/// <summary>Receives UI Automation property-change events (must be public for COM).</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class PropertyChangedHandler : IUIAutomationPropertyChangedEventHandler
{
    private readonly Action _onChanged;

    internal PropertyChangedHandler(Action onChanged)
    {
        _onChanged = onChanged;
    }

    public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, object newValue)
    {
        try
        {
            _onChanged();
        }
        catch (Exception ex)
        {
            Log.Debug($"UI Automation event handler failed: {ex.Message}");
        }
    }
}
