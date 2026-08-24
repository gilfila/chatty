using Flow.Shell.Core;
using static Flow.Shell.Core.ShortcutCatalog;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// Which keys may be the hold-to-talk trigger. The trigger is held for the length of a sentence
/// while the hook passes it through to the focused app, so most of the keyboard is disqualified.
/// </summary>
public sealed class ShortcutCatalogTests
{
    [Fact]
    public void TheDefaultIsRightCtrl()
    {
        Assert.Equal(VK_RCONTROL, Default);
        Assert.True(Validate(Default).IsAllowed);
        Assert.Equal("Right Ctrl", Describe(Default));
    }

    [Theory]
    [InlineData(VK_RMENU)]
    [InlineData(VK_LMENU)]
    public void AltIsRejectedBecauseOfAltGr(ushort key)
    {
        // The locked decision: right Alt is AltGr on international layouts, where holding it is a
        // character modifier and would type into the user's document while they speak.
        var result = Validate(key);

        Assert.False(result.IsAllowed);
        Assert.Equal(ShortcutRejection.AltGrCollision, result.Rejection);
        Assert.Contains("AltGr", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(VK_LWIN)]
    [InlineData(VK_RWIN)]
    public void TheWindowsKeyIsRejectedBecauseReleasingItStealsFocus(ushort key)
    {
        var result = Validate(key);

        Assert.False(result.IsAllowed);
        Assert.Equal(ShortcutRejection.OpensSystemUi, result.Rejection);
    }

    [Theory]
    [InlineData(VK_CAPITAL)]
    [InlineData(VK_NUMLOCK)]
    public void TogglingKeysAreRejectedBecauseTheyLeaveTheKeyboardChanged(ushort key)
    {
        var result = Validate(key);

        Assert.False(result.IsAllowed);
        Assert.Equal(ShortcutRejection.TogglesSystemState, result.Rejection);
    }

    [Theory]
    [InlineData((ushort)0x41)] // A
    [InlineData((ushort)0x5A)] // Z
    [InlineData((ushort)0x30)] // 0
    [InlineData((ushort)0x20)] // Space
    [InlineData((ushort)0x0D)] // Enter
    public void KeysThatTypeAreRejected(ushort key)
    {
        var result = Validate(key);

        Assert.False(result.IsAllowed);
        Assert.Equal(ShortcutRejection.TypesCharacters, result.Rejection);
    }

    [Theory]
    [InlineData(VK_RCONTROL)]
    [InlineData(VK_RSHIFT)]
    [InlineData(VK_LCONTROL)]
    [InlineData(VK_LSHIFT)]
    [InlineData(VK_SCROLL)]
    [InlineData(VK_PAUSE)]
    [InlineData(VK_F13)]
    [InlineData(VK_F24)]
    public void KeysThatTypeNothingAreAllowed(ushort key) => Assert.True(Validate(key).IsAllowed);

    [Fact]
    public void EveryRejectionExplainsItselfInPlainWords()
    {
        foreach (var key in new ushort[] { VK_RMENU, VK_LWIN, VK_CAPITAL, 0x41, 0 })
        {
            var result = Validate(key);

            Assert.False(result.IsAllowed);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
            Assert.NotEqual(ShortcutRejection.None, result.Rejection);
        }
    }

    [Fact]
    public void EveryOfferedOptionIsActuallyAllowed()
    {
        // The catalogue offered to the user and the validator must not disagree.
        foreach (var option in Offered)
        {
            Assert.True(Validate(option.VirtualKey).IsAllowed, $"{option.Name} is offered but rejected");
            Assert.False(string.IsNullOrWhiteSpace(option.Name));
        }
    }

    [Fact]
    public void RightAltIsNotOffered()
    {
        Assert.DoesNotContain(Offered, o => o.VirtualKey == VK_RMENU);
    }

    [Fact]
    public void EveryKeyIsClassifiedWithoutThrowing()
    {
        for (var key = 0; key <= 0xFF; key++)
        {
            _ = Validate((ushort)key);
            _ = Describe((ushort)key);
        }
    }

    [Fact]
    public void NothingIsAllowedByDefault()
    {
        // The validator's fallback must be rejection. A new key that nobody thought about must not
        // silently become a legal trigger.
        var allowed = 0;
        for (var key = 0; key <= 0xFF; key++)
        {
            if (Validate((ushort)key).IsAllowed) allowed++;
        }

        // Right/left Ctrl and Shift, Scroll Lock, Pause, and F13-F24.
        Assert.Equal(18, allowed);
    }

    // ---- Resolve ------------------------------------------------------------

    [Fact]
    public void AStoredAllowedKeyIsHonoured() => Assert.Equal(VK_RSHIFT, Resolve(VK_RSHIFT));

    [Fact]
    public void NoStoredKeyFallsBackToTheDefault() => Assert.Equal(Default, Resolve(null));

    [Fact]
    public void AStoredKeyThatIsNoLongerAllowedFallsBackToTheDefault()
    {
        // A settings file written by an older build defaulted to right Alt. Honouring it would
        // leave the user with a trigger that types into their document on an AltGr layout.
        Assert.Equal(Default, Resolve(VK_RMENU));
        Assert.Equal(Default, Resolve(0x41));
        Assert.Equal(Default, Resolve(0));
    }

    [Fact]
    public void DescribeNeverLeaksARawKeyCode()
    {
        for (var key = 0; key <= 0xFF; key++)
        {
            var text = Describe((ushort)key);

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("0x", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- Next ---------------------------------------------------------------

    [Fact]
    public void ChangingTheShortcutCyclesThroughTheOfferedList()
    {
        var seen = new List<ushort>();
        var key = Default;

        for (var i = 0; i < Offered.Count; i++)
        {
            seen.Add(key);
            key = Next(key);
        }

        Assert.Equal(Default, key); // wrapped all the way round
        Assert.Equal(Offered.Count, seen.Distinct().Count());
        Assert.All(seen, k => Assert.True(Validate(k).IsAllowed));
    }

    [Fact]
    public void CyclingFromAnUnknownKeyLandsOnTheDefault() => Assert.Equal(Default, Next(0x41));

    [Fact]
    public void EveryKeyCyclingCanReachIsAllowed()
    {
        for (var key = 0; key <= 0xFF; key++)
        {
            Assert.True(Validate(Next((ushort)key)).IsAllowed);
        }
    }

    [Fact]
    public void TheExtendedFunctionKeysAreNamedCorrectly()
    {
        Assert.Equal("F13", Describe(VK_F13));
        Assert.Equal("F24", Describe(VK_F24));
    }
}
