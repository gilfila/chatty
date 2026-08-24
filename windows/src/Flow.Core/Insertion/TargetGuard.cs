using Flow.Core.Abstractions;

namespace Flow.Core.Insertion;

public enum TargetVerdict { Ok, SecureField, Elevated, Unresolvable }

/// <summary>Pure policy deciding whether a foreground target may receive dictated text.
/// Flow never asks for elevation to reach a protected window; it declines instead.</summary>
public static class TargetGuard
{
    public static TargetVerdict EvaluateForCapture(TargetDescriptor? target)
    {
        if (target is null || target.WindowHandle == 0) return TargetVerdict.Unresolvable;
        if (target.IsSecureField) return TargetVerdict.SecureField;
        if (target.IsElevated) return TargetVerdict.Elevated;
        return TargetVerdict.Ok;
    }

    /// <summary>Pre-paste check: the current foreground must be the same window and process
    /// captured at record start, and must still pass the capture policy.</summary>
    public static TargetVerdict EvaluateForPaste(TargetDescriptor captured, TargetDescriptor? current, bool stillForeground)
    {
        if (current is null || !stillForeground) return TargetVerdict.Unresolvable;
        if (current.WindowHandle != captured.WindowHandle || current.ProcessId != captured.ProcessId)
            return TargetVerdict.Unresolvable;
        return EvaluateForCapture(current);
    }
}
