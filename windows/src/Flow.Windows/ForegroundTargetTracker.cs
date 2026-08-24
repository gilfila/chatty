using System.Runtime.InteropServices;
using Flow.Core.Abstractions;
using static Flow.Windows.Interop.NativeMethods;

namespace Flow.Windows;

/// <summary>
/// Resolves the foreground window into a TargetDescriptor: window, process, elevation
/// (integrity level above medium), and field classification.
///
/// Field classification is fail-closed per the locked product decision: the focused
/// element must be positively classified by UI Automation as a text-editable,
/// non-password field, or capture returns null and no recording starts. The Win32
/// ES_PASSWORD style check stays as a second, independent password signal.
/// </summary>
public sealed class ForegroundTargetTracker : ITargetTracker
{
    private readonly UiaFieldInspector _inspector = new();

    /// <summary>UIA runtime-id signature of the most recently captured field. Interim home:
    /// this belongs in TargetDescriptor (see TargetIdentityGapTests) once the Core contract
    /// gains a field identity; until then the shell records it for diagnostics.</summary>
    public string? LastFieldSignature { get; private set; }

    public TargetDescriptor? CaptureForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;

        var threadId = GetWindowThreadProcessId(hwnd, out var pid);
        if (threadId == 0 || pid == 0) return null;

        var field = _inspector.TryClassifyFocusedField();
        if (field is null || !field.IsTextEditable) return null; // unclassifiable → refuse
        LastFieldSignature = field.FieldSignature;

        return new TargetDescriptor(
            hwnd,
            (int)pid,
            (int)threadId,
            IsElevated: IsProcessElevated(pid),
            IsSecureField: field.IsPassword || FocusedControlIsPasswordEdit(threadId),
            ProcessName: GetProcessName(pid));
    }

    public bool IsStillForeground(TargetDescriptor captured) =>
        GetForegroundWindow() == captured.WindowHandle;

    private static bool FocusedControlIsPasswordEdit(uint threadId)
    {
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(threadId, ref info) || info.hwndFocus == 0) return false;

        var buffer = new char[64];
        var len = GetClassNameW(info.hwndFocus, buffer, buffer.Length);
        if (len <= 0) return false;
        var className = new string(buffer, 0, len);
        if (!className.Contains("Edit", StringComparison.OrdinalIgnoreCase)) return false;

        var style = (long)GetWindowLongPtrW(info.hwndFocus, GWL_STYLE);
        return (style & ES_PASSWORD) != 0;
    }

    /// <summary>True when the process runs above medium integrity (elevated/system).
    /// If the token cannot be read at all, the process is almost certainly higher-privilege
    /// than us — treat it as elevated and refuse rather than guess.</summary>
    private static bool IsProcessElevated(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return true;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return true;
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, 0, 0, out var needed);
                if (needed == 0) return true;
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _)) return true;
                    // TOKEN_MANDATORY_LABEL: first field is the SID pointer.
                    var sid = Marshal.ReadIntPtr(buffer);
                    var countPtr = GetSidSubAuthorityCount(sid);
                    var count = Marshal.ReadByte(countPtr);
                    var ridPtr = GetSidSubAuthority(sid, (uint)(count - 1));
                    var rid = (uint)Marshal.ReadInt32(ridPtr);
                    return rid > SECURITY_MANDATORY_MEDIUM_RID;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static string GetProcessName(uint pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return "";
        try
        {
            var buffer = new char[512];
            var size = (uint)buffer.Length;
            if (!QueryFullProcessImageNameW(process, 0, buffer, ref size)) return "";
            var path = new string(buffer, 0, (int)size);
            return Path.GetFileName(path);
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
