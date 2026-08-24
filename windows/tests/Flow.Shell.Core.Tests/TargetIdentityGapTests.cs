using Flow.Core.Abstractions;
using Flow.Core.Insertion;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// Documents a gap between <see cref="TargetGuard"/> and delivery-plan invariant 3, which is
/// written in terms of the <i>field</i> and not the window:
///
/// <para><i>"The app never inserts into a different foreground field than the one captured at
/// recording start."</i></para>
///
/// <para>
/// <see cref="TargetDescriptor"/> carries no field-level identity — only the window handle,
/// process and thread. So two different edit controls inside one window produce two identical
/// descriptors, and the guard cannot tell them apart. The macOS build does not have this gap: its
/// <c>FocusTarget</c> carries an opaque accessibility element signature and compares on it.
/// </para>
///
/// <para>
/// These tests assert the behaviour as it currently stands rather than as it should be, so the
/// suite stays honest and green. When <see cref="TargetDescriptor"/> gains a field signature,
/// <see cref="FocusMovingBetweenTwoFieldsInOneWindow_IsCurrentlyIndistinguishable"/> will start
/// failing, which is exactly the signal wanted. Owner of the fix: the speech/insertion workstream,
/// since <see cref="TargetDescriptor"/> and <see cref="ITargetTracker"/> are theirs.
/// </para>
/// </summary>
public sealed class TargetIdentityGapTests
{
    private static TargetDescriptor Field(string process = "Outlook") => new(
        WindowHandle: 0x1234,
        ProcessId: 4242,
        ThreadId: 99,
        IsElevated: false,
        IsSecureField: false,
        ProcessName: process);

    [Fact]
    public void FocusMovingToAnotherWindow_IsCaughtToday()
    {
        var captured = Field();
        var elsewhere = captured with { WindowHandle = 0x9999, ProcessId = 7777 };

        Assert.Equal(
            TargetVerdict.Unresolvable,
            TargetGuard.EvaluateForPaste(captured, elsewhere, stillForeground: true));
    }

    [Fact]
    public void ForegroundLeavingEntirely_IsCaughtToday()
    {
        var captured = Field();

        Assert.Equal(
            TargetVerdict.Unresolvable,
            TargetGuard.EvaluateForPaste(captured, null, stillForeground: false));

        Assert.Equal(
            TargetVerdict.Unresolvable,
            TargetGuard.EvaluateForPaste(captured, captured, stillForeground: false));
    }

    /// <summary>
    /// The gap. Tab from the message body to the subject line in one Outlook window, or from a
    /// chat composer to the search box in one Electron window, and every field the descriptor
    /// carries is unchanged — so the guard says paste.
    /// </summary>
    [Fact]
    public void FocusMovingBetweenTwoFieldsInOneWindow_IsCurrentlyIndistinguishable()
    {
        var messageBody = Field();
        var subjectLine = Field(); // a different control, same window, same process, same thread

        Assert.Equal(messageBody, subjectLine);

        // Invariant 3 wants TargetVerdict.Unresolvable here. It cannot be reached with the
        // information TargetDescriptor currently carries.
        Assert.Equal(
            TargetVerdict.Ok,
            TargetGuard.EvaluateForPaste(messageBody, subjectLine, stillForeground: true));
    }

    /// <summary>
    /// What closing the gap needs. A single opaque, stable-per-element value — the UI Automation
    /// runtime id is the natural source — is enough, because equality is the whole mechanism.
    /// </summary>
    [Fact]
    public void AFieldSignatureWouldCloseIt()
    {
        var messageBody = (Descriptor: Field(), FieldSignature: "uia:42.17.3");
        var subjectLine = (Descriptor: Field(), FieldSignature: "uia:42.17.9");

        Assert.Equal(messageBody.Descriptor, subjectLine.Descriptor);
        Assert.NotEqual(messageBody.FieldSignature, subjectLine.FieldSignature);
    }
}
