using System.Runtime.InteropServices;
using static Flow.Windows.Interop.NativeMethods;

namespace Flow.Windows;

/// <summary>
/// Injects Ctrl+V via SendInput. The session machine guarantees this only runs after the
/// hold-to-talk modifier is released, so the held trigger can't join the chord; as a last
/// line of defense we also bail if a stray Alt/Win modifier is physically down.
/// </summary>
public sealed class PasteInjector : Flow.Core.Abstractions.IPasteInjector
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL_STATE = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    public bool SendPaste()
    {
        // A physically-held modifier would merge into the chord (Ctrl+Shift+V is
        // paste-without-formatting in many apps; Alt/Win change it entirely) and our
        // injected key-ups would desync the user's real key state. Refuse; the text
        // stays recoverable via Copy Last.
        if (IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN) ||
            IsDown(VK_CONTROL_STATE) || IsDown(VK_SHIFT)) return false;

        var inputs = new INPUT[]
        {
            Key(VK_CONTROL, up: false),
            Key(VK_V, up: false),
            Key(VK_V, up: true),
            Key(VK_CONTROL, up: true),
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        return sent == inputs.Length;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 },
        },
    };
}
