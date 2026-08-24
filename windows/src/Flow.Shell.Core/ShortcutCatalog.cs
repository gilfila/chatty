namespace Flow.Shell.Core;

/// <summary>Why a key cannot be Flow's hold-to-talk trigger.</summary>
public enum ShortcutRejection
{
    None,

    /// <summary>On international layouts this key is AltGr and types characters when held.</summary>
    AltGrCollision,

    /// <summary>Toggles a system state, so holding it leaves the machine changed.</summary>
    TogglesSystemState,

    /// <summary>Releasing it opens a system UI that steals focus from the target field.</summary>
    OpensSystemUi,

    /// <summary>Types a character, so holding it fills the user's field while they speak.</summary>
    TypesCharacters,

    /// <summary>Not a key Flow can observe as a clean hold.</summary>
    NotHoldable,
}

/// <summary>A key Flow will accept as the hold-to-talk trigger.</summary>
public sealed record ShortcutOption(ushort VirtualKey, string Name, string? Note = null);

/// <summary>The result of checking a candidate trigger key.</summary>
public readonly record struct ShortcutValidation(bool IsAllowed, ShortcutRejection Rejection, string Reason)
{
    public static readonly ShortcutValidation Allowed = new(true, ShortcutRejection.None, string.Empty);
}

/// <summary>
/// Which keys may be the hold-to-talk trigger, and what to call them.
/// </summary>
/// <remarks>
/// The trigger is held down for the length of a sentence while the hook passes it through to
/// whatever app is focused. That rules out far more keys than it first appears:
///
/// <list type="bullet">
/// <item><description>Anything that <b>types</b> fills the user's field with repeats while they
/// speak. That is every letter, digit and punctuation key.</description></item>
/// <item><description>Right Alt is <b>AltGr</b> on international layouts — it is a character
/// modifier there, not a spare modifier. This is why it was rejected as the default.</description></item>
/// <item><description>Anything that <b>toggles</b> leaves the machine in a changed state after a
/// dictation. Caps Lock is the obvious trap.</description></item>
/// <item><description>The Windows keys <b>open the Start menu on release</b>, which steals focus
/// from the very field Flow captured.</description></item>
/// </list>
///
/// <para>
/// What survives is the right-hand modifiers other than Alt, the keyboard-lock keys that no longer
/// do anything useful, and the extended function keys. Right Ctrl is the default because it is
/// present on essentially every keyboard, types nothing on any layout, and is rarely held alone.
/// </para>
/// </remarks>
public static class ShortcutCatalog
{
    public const ushort VK_LSHIFT = 0xA0;
    public const ushort VK_RSHIFT = 0xA1;
    public const ushort VK_LCONTROL = 0xA2;
    public const ushort VK_RCONTROL = 0xA3;
    public const ushort VK_LMENU = 0xA4;
    public const ushort VK_RMENU = 0xA5;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_RWIN = 0x5C;
    public const ushort VK_CAPITAL = 0x14;
    public const ushort VK_NUMLOCK = 0x90;
    public const ushort VK_SCROLL = 0x91;
    public const ushort VK_PAUSE = 0x13;
    public const ushort VK_F13 = 0x7C;
    public const ushort VK_F24 = 0x87;

    /// <summary>Right Ctrl. Present on every keyboard, types nothing on any layout.</summary>
    public const ushort Default = VK_RCONTROL;

    /// <summary>What first-run offers, best first.</summary>
    public static IReadOnlyList<ShortcutOption> Offered { get; } = new[]
    {
        new ShortcutOption(VK_RCONTROL, "Right Ctrl"),
        new ShortcutOption(VK_RSHIFT, "Right Shift"),
        new ShortcutOption(VK_SCROLL, "Scroll Lock", "Rarely used by anything else."),
        new ShortcutOption(VK_PAUSE, "Pause"),
        new ShortcutOption(VK_F13, "F13", "Only on extended keyboards."),
    };

    public static ShortcutValidation Validate(ushort virtualKey) => virtualKey switch
    {
        VK_RMENU or VK_LMENU => new ShortcutValidation(
            false,
            ShortcutRejection.AltGrCollision,
            "Alt is AltGr on many keyboard layouts, so holding it would type characters."),

        VK_LWIN or VK_RWIN => new ShortcutValidation(
            false,
            ShortcutRejection.OpensSystemUi,
            "The Windows key opens the Start menu when you let go, which would move you out of your text field."),

        VK_CAPITAL or VK_NUMLOCK => new ShortcutValidation(
            false,
            ShortcutRejection.TogglesSystemState,
            "That key toggles, so dictating would leave your keyboard switched."),

        VK_RCONTROL or VK_RSHIFT or VK_LCONTROL or VK_LSHIFT => ShortcutValidation.Allowed,

        VK_SCROLL or VK_PAUSE => ShortcutValidation.Allowed,

        >= VK_F13 and <= VK_F24 => ShortcutValidation.Allowed,

        0 => new ShortcutValidation(
            false, ShortcutRejection.NotHoldable, "That is not a key Flow can watch for."),

        _ => new ShortcutValidation(
            false,
            ShortcutRejection.TypesCharacters,
            "That key types something, so holding it would fill your text field while you speak."),
    };

    /// <summary>Display name for the panel and the tray tip. Never a raw virtual-key code.</summary>
    public static string Describe(ushort virtualKey)
    {
        foreach (var option in Offered)
        {
            if (option.VirtualKey == virtualKey) return option.Name;
        }

        return virtualKey switch
        {
            VK_LCONTROL => "Left Ctrl",
            VK_LSHIFT => "Left Shift",
            VK_RMENU => "Right Alt",
            VK_LMENU => "Left Alt",
            VK_LWIN => "Left Windows",
            VK_RWIN => "Right Windows",
            VK_CAPITAL => "Caps Lock",
            VK_NUMLOCK => "Num Lock",
            >= VK_F13 and <= VK_F24 => $"F{13 + (virtualKey - VK_F13)}",
            _ => "that key",
        };
    }

    /// <summary>
    /// The next trigger to offer when the user asks for a different one.
    /// </summary>
    /// <remarks>
    /// Cycling through a short vetted list is what the panel can actually do with its single
    /// button. Live key capture would read better, but it means listening for arbitrary keystrokes
    /// with no way to verify the behaviour off Windows — and a capture mode that mis-binds leaves
    /// the user with no working shortcut and no obvious way back.
    /// </remarks>
    public static ushort Next(ushort current)
    {
        for (var i = 0; i < Offered.Count; i++)
        {
            if (Offered[i].VirtualKey == current)
            {
                return Offered[(i + 1) % Offered.Count].VirtualKey;
            }
        }

        return Default;
    }

    /// <summary>
    /// The trigger to actually use. Falls back to <see cref="Default"/> rather than honouring a
    /// stored value that is no longer allowed, so a settings file written by an older build — or
    /// edited by hand — cannot leave the user with a trigger that types into their document.
    /// </summary>
    public static ushort Resolve(ushort? stored) =>
        stored is { } key && Validate(key).IsAllowed ? key : Default;
}
