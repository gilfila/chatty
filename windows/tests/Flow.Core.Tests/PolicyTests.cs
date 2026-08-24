using Flow.Core.Abstractions;
using Flow.Core.Insertion;
using Xunit;

namespace Flow.Core.Tests;

public class TargetGuardTests
{
    private static readonly TargetDescriptor Editor = FakeTargets.Editor();

    [Fact]
    public void Capture_NullOrZeroHandle_IsUnresolvable()
    {
        Assert.Equal(TargetVerdict.Unresolvable, TargetGuard.EvaluateForCapture(null));
        Assert.Equal(TargetVerdict.Unresolvable, TargetGuard.EvaluateForCapture(Editor with { WindowHandle = 0 }));
    }

    [Fact]
    public void Capture_SecureField_WinsOverElevated()
    {
        var both = Editor with { IsSecureField = true, IsElevated = true };
        Assert.Equal(TargetVerdict.SecureField, TargetGuard.EvaluateForCapture(both));
    }

    [Fact]
    public void Paste_DifferentWindow_IsRejected()
    {
        var current = Editor with { WindowHandle = 999 };
        Assert.Equal(TargetVerdict.Unresolvable, TargetGuard.EvaluateForPaste(Editor, current, stillForeground: true));
    }

    [Fact]
    public void Paste_SameWindowDifferentProcess_IsRejected()
    {
        var current = Editor with { ProcessId = 4242 };
        Assert.Equal(TargetVerdict.Unresolvable, TargetGuard.EvaluateForPaste(Editor, current, stillForeground: true));
    }

    [Fact]
    public void Paste_NotForeground_IsRejected()
    {
        Assert.Equal(TargetVerdict.Unresolvable, TargetGuard.EvaluateForPaste(Editor, Editor, stillForeground: false));
    }

    [Fact]
    public void Paste_SameTargetStillForeground_IsAllowed()
    {
        Assert.Equal(TargetVerdict.Ok, TargetGuard.EvaluateForPaste(Editor, Editor, stillForeground: true));
    }
}

public class ClipboardRestorePolicyTests
{
    [Fact]
    public void SequenceChanged_MeansLeaveAlone_EvenWithBackup()
    {
        var token = new ClipboardToken(7);
        Assert.Equal(RestoreDecision.LeaveAlone, ClipboardRestorePolicy.Decide(token, currentSequence: 8, hadBackup: true));
    }

    [Fact]
    public void SequenceUnchanged_WithBackup_Restores()
    {
        var token = new ClipboardToken(7);
        Assert.Equal(RestoreDecision.RestoreBackup, ClipboardRestorePolicy.Decide(token, currentSequence: 7, hadBackup: true));
    }

    [Fact]
    public void SequenceUnchanged_WithoutBackup_LeavesTranscriptInPlace()
    {
        var token = new ClipboardToken(7);
        Assert.Equal(RestoreDecision.LeaveAlone, ClipboardRestorePolicy.Decide(token, currentSequence: 7, hadBackup: false));
    }
}
