using AppleMusicBridge;
using Xunit;

namespace AppleMusicBridge.Tests;

public class PlayerControlMatchingTests
{
    // A toggle in the player bar at the top of the window.
    private static ControlCandidate Button(
        string name,
        string id = "",
        bool toggle = false,
        bool invoke = true,
        double? top = 0.03,
        string help = "") =>
        new(name, id, help, "Button", toggle, invoke, true, top);

    [Fact]
    public void Picks_the_player_bar_shuffle_toggle()
    {
        var playerShuffle = Button("Shuffle", "ShuffleButton", toggle: true, invoke: false);
        Assert.Same(playerShuffle, PlayerControlMatching.Choose(PlayerControlKind.Shuffle, [playerShuffle]));
    }

    [Fact]
    public void Never_picks_an_album_pages_play_shuffled_button()
    {
        var albumShuffle = Button("Shuffle", top: 0.45);
        Assert.Null(PlayerControlMatching.Choose(PlayerControlKind.Shuffle, [albumShuffle]));
        Assert.Equal(0, PlayerControlMatching.Score(PlayerControlKind.Shuffle, albumShuffle));
    }

    [Fact]
    public void Prefers_the_player_bar_toggle_over_other_matches()
    {
        var albumShuffle = Button("Shuffle", top: 0.45);
        var playerShuffle = Button("Shuffle", toggle: true, invoke: false, top: 0.02);
        var somethingElse = Button("Play", top: 0.02);
        Assert.Same(playerShuffle, PlayerControlMatching.Choose(PlayerControlKind.Shuffle, [albumShuffle, somethingElse, playerShuffle]));
    }

    [Fact]
    public void Matches_on_the_automation_id_alone()
    {
        var button = Button("", "TransportRepeatButton");
        Assert.Same(button, PlayerControlMatching.Choose(PlayerControlKind.Repeat, [button]));
    }

    [Theory]
    [InlineData("Zufällige Wiedergabe")]
    [InlineData("Lecture aléatoire")]
    [InlineData("Reproducción aleatoria")]
    [InlineData("シャッフル")]
    public void Recognizes_localized_shuffle_names(string name) =>
        Assert.True(PlayerControlMatching.Score(PlayerControlKind.Shuffle, Button(name, toggle: true)) > 0);

    [Theory]
    [InlineData("Wiederholen")]
    [InlineData("Répéter")]
    [InlineData("Repetir")]
    [InlineData("リピート")]
    public void Recognizes_localized_repeat_names(string name) =>
        Assert.True(PlayerControlMatching.Score(PlayerControlKind.Repeat, Button(name)) > 0);

    [Fact]
    public void Shuffle_and_repeat_do_not_mix_up()
    {
        var shuffle = Button("Shuffle", toggle: true);
        var repeat = Button("Repeat");
        Assert.Same(shuffle, PlayerControlMatching.Choose(PlayerControlKind.Shuffle, [repeat, shuffle]));
        Assert.Same(repeat, PlayerControlMatching.Choose(PlayerControlKind.Repeat, [shuffle, repeat]));
    }

    [Fact]
    public void Position_unknown_only_accepts_toggles()
    {
        Assert.True(PlayerControlMatching.Score(PlayerControlKind.Shuffle, Button("Shuffle", toggle: true, top: null)) > 0);
        Assert.Equal(0, PlayerControlMatching.Score(PlayerControlKind.Shuffle, Button("Shuffle", top: null)));
    }

    [Fact]
    public void Ignores_controls_that_cannot_be_pressed_or_do_not_match()
    {
        var label = new ControlCandidate("Shuffle", "", "", "TextBlock", false, false, false, 0.02);
        Assert.Equal(0, PlayerControlMatching.Score(PlayerControlKind.Shuffle, label));
        Assert.Null(PlayerControlMatching.Choose(PlayerControlKind.Repeat, [Button("Next"), Button("Previous")]));
    }

    [Theory]
    [InlineData(1, null, true)]
    [InlineData(0, null, false)]
    [InlineData(null, 0x10u, true)] // STATE_SYSTEM_CHECKED
    [InlineData(null, 0x08u, true)] // STATE_SYSTEM_PRESSED
    [InlineData(null, 0x100000u, false)] // focusable, not checked
    public void Shuffle_state_from_toggle_or_msaa_state(int? toggle, uint? legacy, bool expected) =>
        Assert.Equal(expected, PlayerControlMatching.ParseShuffle(toggle, legacy, "Shuffle"));

    [Theory]
    [InlineData("Shuffle On", true)]
    [InlineData("Shuffle off", false)]
    [InlineData("Turn shuffle on or off", null)]
    [InlineData("Shuffle", null)]
    public void Shuffle_state_from_text(string text, bool? expected) =>
        Assert.Equal(expected, PlayerControlMatching.ParseShuffle(null, null, text));

    [Theory]
    [InlineData(0, "Off")]
    [InlineData(1, "All")]
    [InlineData(2, "One")]
    public void Repeat_state_from_a_three_state_toggle(int toggle, string expected) =>
        Assert.Equal(Enum.Parse<RepeatMode>(expected), PlayerControlMatching.ParseRepeat(toggle, null, "Repeat"));

    [Theory]
    [InlineData("Repeat One", "One")]
    [InlineData("Repeat All", "All")]
    [InlineData("Repeat Off", "Off")]
    [InlineData("Titel wiederholen", "One")]
    public void Repeat_state_from_text_wins_over_a_two_state_toggle(string text, string expected) =>
        Assert.Equal(Enum.Parse<RepeatMode>(expected), PlayerControlMatching.ParseRepeat(1, null, text));

    [Fact]
    public void Descriptive_repeat_text_is_ignored()
    {
        Assert.Equal(RepeatMode.All, PlayerControlMatching.ParseRepeat(1, null, "Repeat: off, all or one"));
        Assert.Null(PlayerControlMatching.ParseRepeat(null, null, "Repeat"));
    }
}
