using ConnectOnion.WinUIClient.Models;

namespace ConnectOnion.WinUIClient.UnitTests.Models;

public sealed class KeyChordTests
{
    private const int VkN = 'N';
    private const int VkComma = 188;
    private const int VkBacktick = 192;
    private const int VkEqual = 187;
    private const int VkMinus = 189;
    private const int VkF11 = 122;
    private const int VkNumpadAdd = 107;
    private const int VkNumpadSubtract = 109;
    private const int VkControl = 17;
    private const int VkShiftKey = 16;

    [Theory]
    [InlineData(true, false, false, VkN, "Ctrl+N")]
    [InlineData(true, true, false, VkEqual, "Ctrl+Shift+=")]
    [InlineData(true, false, false, VkComma, "Ctrl+,")]
    [InlineData(true, false, false, VkBacktick, "Ctrl+`")]
    [InlineData(false, false, false, VkF11, "F11")]
    [InlineData(true, true, true, VkN, "Ctrl+Shift+Alt+N")]
    public void Canonical_AnyChord_RoundTripsThroughTryParse(
        bool ctrl, bool shift, bool alt, int key, string expected)
    {
        var chord = new KeyChord(ctrl, shift, alt, key);

        Assert.Equal(expected, chord.Canonical);
        Assert.True(KeyChord.TryParse(expected, out var parsed));
        Assert.Equal(chord, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]                 // modifiers alone are not a chord
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+N+O")]             // two real keys
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl+MediaPlay")]       // outside the dispatchable table
    public void TryParse_UnusableText_Fails(string text)
    {
        Assert.False(KeyChord.TryParse(text, out var chord));
        Assert.Equal(KeyChord.None, chord);
    }

    [Fact]
    public void TryParse_ModifierOrderAndCasing_AreNormalized()
    {
        Assert.True(KeyChord.TryParse("shift+CTRL+n", out var chord));

        Assert.Equal(new KeyChord(Ctrl: true, Shift: true, Alt: false, VkN), chord);
        Assert.Equal("Ctrl+Shift+N", chord.Canonical);
    }

    [Fact]
    public void ParseOrNone_Junk_ReturnsNoneSoAnOverrideCanFallBackToItsDefault()
        => Assert.Equal(KeyChord.None, KeyChord.ParseOrNone("!!! not a chord !!!"));

    [Theory]
    [InlineData(VkControl)]
    [InlineData(VkShiftKey)]
    [InlineData(18)]
    public void FromKeyEvent_ModifierPressedAlone_IsNotAChord(int modifierKey)
        => Assert.Equal(KeyChord.None, KeyChord.FromKeyEvent(modifierKey, ctrl: true, shift: false, alt: false));

    /// <summary>The zoom handlers accepted numpad +/- alongside the OEM keys before the resolver
    /// existed. A chord names one key, so the alias has to fold — otherwise numpad zoom dies.</summary>
    [Theory]
    [InlineData(VkNumpadAdd, VkEqual)]
    [InlineData(VkNumpadSubtract, VkMinus)]
    public void FromKeyEvent_NumpadPlusOrMinus_FoldsOntoTheOemTwin(int pressed, int expected)
    {
        var chord = KeyChord.FromKeyEvent(pressed, ctrl: true, shift: false, alt: false);

        Assert.Equal(new KeyChord(Ctrl: true, Shift: false, Alt: false, expected), chord);
    }

    [Fact]
    public void FromKeyEvent_KeyOutsideTheTable_IsNotAChord()
        => Assert.Equal(KeyChord.None, KeyChord.FromKeyEvent(0xB3, ctrl: true, shift: false, alt: false));

    [Fact]
    public void ToBinding_Chord_RendersTheKeycapsTheDialogBindsTo()
    {
        var binding = new KeyChord(Ctrl: true, Shift: true, Alt: false, VkN).ToBinding();

        Assert.Equal(new[] { "Ctrl", "Shift", "N" }, binding.Keys);
        Assert.Equal("Ctrl + Shift + N", binding.DisplayText);
    }

    [Fact]
    public void None_IsEmptyAndRendersNothing()
    {
        Assert.True(KeyChord.None.IsEmpty);
        Assert.Empty(KeyChord.None.DisplayKeys);
        Assert.Equal("", KeyChord.None.Canonical);
    }
}
