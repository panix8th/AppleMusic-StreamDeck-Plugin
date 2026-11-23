using System.Text.RegularExpressions;

namespace AppleMusicBridge;

// Decides which of Apple Music's buttons are its player-bar Shuffle and Repeat buttons, and what state they
// show. Pure logic over values read through UI Automation; no Windows APIs here (compiled into the unit tests).

internal enum PlayerControlKind
{
    Shuffle,
    Repeat,
}

/// <summary>One button in Apple Music's window, as UI Automation describes it.</summary>
/// <param name="RelativeTop">
/// Distance of the button's top edge from the window's top edge, as a fraction of the window height
/// (0 = top). Null when unknown (for example while the window is minimized).
/// </param>
internal sealed record ControlCandidate(
    string Name,
    string AutomationId,
    string HelpText,
    string ClassName,
    bool CanToggle,
    bool CanInvoke,
    bool CanDoDefaultAction,
    double? RelativeTop);

internal static partial class PlayerControlMatching
{
    /// <summary>
    /// The transport controls sit in the bar at the top of Apple Music's window. Album and playlist pages have
    /// their own "Shuffle" (play shuffled) buttons further down, which must never be picked.
    /// </summary>
    public const double PlayerBarMaxRelativeTop = 0.25;

    // AutomationIds are usually English; names follow the app's language. Lower case, matched as substrings.
    private static readonly string[] ShuffleWords =
    [
        "shuffle", "zufall", "zufällig", "aléatoire", "aleatori", "aleatóri", "casuale", "willekeurig",
        "blanda", "satunnai", "losow", "náhodn", "シャッフル", "随机", "隨機", "셔플", "перемеш",
    ];

    private static readonly string[] RepeatWords =
    [
        "repeat", "wiederhol", "répét", "repetir", "repetição", "ripeti", "herhaal", "upprepa", "gjenta",
        "gentag", "toista", "powtarz", "opakov", "リピート", "重复", "重複", "반복", "повтор",
    ];

    /// <summary>The best candidate for the control, or null when none is convincing.</summary>
    public static ControlCandidate? Choose(PlayerControlKind kind, IEnumerable<ControlCandidate> candidates)
    {
        ControlCandidate? best = null;
        var bestScore = 0;
        foreach (var candidate in candidates)
        {
            var score = Score(kind, candidate);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>0 = not this control; higher = more likely.</summary>
    public static int Score(PlayerControlKind kind, ControlCandidate candidate)
    {
        var words = kind == PlayerControlKind.Shuffle ? ShuffleWords : RepeatWords;
        var idMatch = ContainsAny(candidate.AutomationId, words);
        var textMatch = ContainsAny(candidate.Name, words) || ContainsAny(candidate.HelpText, words);
        if (!idMatch && !textMatch)
        {
            return 0;
        }

        if (!candidate.CanToggle && !candidate.CanInvoke && !candidate.CanDoDefaultAction)
        {
            return 0;
        }

        if (candidate.RelativeTop is { } top)
        {
            // Below the player bar: a page's "play shuffled" button, never the player's toggle.
            if (top < 0 || top > PlayerBarMaxRelativeTop)
            {
                return 0;
            }
        }
        else if (!candidate.CanToggle)
        {
            // Position unknown (minimized window): only accept the toggle kind of button, which page buttons are not.
            return 0;
        }

        var score = 1;
        if (idMatch)
        {
            score += 30;
        }

        if (textMatch)
        {
            score += 20;
        }

        if (candidate.CanToggle)
        {
            score += 15;
        }

        if (candidate.RelativeTop is not null)
        {
            score += 10;
        }

        return score;
    }

    public static bool MentionsShuffleOrRepeat(string? text) =>
        ContainsAny(text, ShuffleWords) || ContainsAny(text, RepeatWords);

    /// <summary>Shuffle state from what the button reports; null when it cannot be told.</summary>
    /// <param name="toggleState">UI Automation ToggleState: 0 off, 1 on, 2 indeterminate; null if not a toggle.</param>
    /// <param name="legacyState">MSAA state flags (LegacyIAccessible), if available.</param>
    public static bool? ParseShuffle(int? toggleState, uint? legacyState, params string?[] texts)
    {
        switch (toggleState)
        {
            case 0:
                return false;
            case 1:
                return true;
        }

        if (legacyState is { } state && (state & (StateSystemChecked | StateSystemPressed)) != 0)
        {
            return true;
        }

        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var off = OffWord().IsMatch(text);
            var on = OnWord().IsMatch(text);
            if (off != on)
            {
                // Text naming both ("Turn shuffle on or off") says nothing about the state.
                return on;
            }
        }

        return toggleState is null && legacyState is not null ? false : null;
    }

    /// <summary>Repeat mode from what the button reports; null when it cannot be told.</summary>
    public static RepeatMode? ParseRepeat(int? toggleState, uint? legacyState, params string?[] texts)
    {
        // Text first: a three-way button often says what it is set to ("Repeat One"). Text naming several
        // modes ("Repeat: off, all or one") says nothing and is skipped.
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var one = OneWord().IsMatch(text);
            var all = AllWord().IsMatch(text);
            var off = OffWord().IsMatch(text);
            if (one && !all && !off)
            {
                return RepeatMode.One;
            }

            if (all && !one && !off)
            {
                return RepeatMode.All;
            }

            if (off && !one && !all)
            {
                return RepeatMode.Off;
            }
        }

        switch (toggleState)
        {
            case 0:
                return RepeatMode.Off;
            case 1:
                return RepeatMode.All;
            case 2:
                // Three-state toggle: unchecked -> checked -> indeterminate, like off -> all -> one.
                return RepeatMode.One;
        }

        if (legacyState is { } state)
        {
            return (state & (StateSystemChecked | StateSystemPressed)) != 0 ? RepeatMode.All : RepeatMode.Off;
        }

        return null;
    }

    private const uint StateSystemPressed = 0x8;
    private const uint StateSystemChecked = 0x10;

    private static bool ContainsAny(string? text, string[] words)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var word in words)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\b(on|ein|an|activ[ée]|activado|attivo|aan)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnWord();

    [GeneratedRegex(@"\b(off|aus|désactivé|desactivado|disattivato|uit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OffWord();

    [GeneratedRegex(@"(\bone\b|\b1\b|\bsingle\b|\b(song|track|titel)\b|\beinen\b|\bun titre\b|\buna\b|一曲|单曲|單曲|1곡)", RegexOptions.IgnoreCase)]
    private static partial Regex OneWord();

    [GeneratedRegex(@"\b(all|alle|tout|todo|todas|tutti|tutte)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AllWord();
}
