using Flow.Core.Insertion;

namespace Flow.Shell.Core;

/// <summary>What Flow will do about the target that held focus when the shortcut went down.</summary>
public enum TargetAdmission
{
    /// <summary>Dictate, and paste back into this field on release.</summary>
    Dictate,

    /// <summary>Dictate, but do not attempt to paste. The text goes straight to Copy last.</summary>
    RecordOnly,

    /// <summary>Do not listen at all.</summary>
    Refuse,
}

/// <summary>The press-edge verdict, decided before the user says anything.</summary>
/// <param name="Reason">One sentence for the panel. Empty when there is nothing to say.</param>
public readonly record struct CaptureDecision(TargetAdmission Admission, string Reason);

/// <summary>
/// Turns the pre-paste target policy into a decision the user is told at the moment they press,
/// rather than after they have finished speaking.
/// </summary>
/// <remarks>
/// This is a product decision layered on <see cref="TargetGuard"/>, not a second safety check.
/// The safety answer for an administrator window is already settled — Flow declines, and never
/// asks to be elevated merely to paste. What is left is a timing question, and the timing is the
/// whole experience:
///
/// <para>
/// Everything <see cref="TargetGuard.EvaluateForCapture"/> can refuse is knowable the instant the
/// shortcut goes down. Letting someone dictate a paragraph into an administrator window and only
/// then telling them it cannot be typed is a bad trade against saying it before they open their
/// mouth. So a refusable-but-recordable target degrades to <see cref="TargetAdmission.RecordOnly"/>
/// with the reason on screen while they speak, and the transcript lands in Copy last exactly as
/// the invariants require.
/// </para>
///
/// <para>
/// Two targets refuse outright. A password field is the obvious one: Flow does not listen, and
/// nothing is kept, because recording a password into a local transcript store would be worse than
/// useless even though it would technically be recoverable.
/// </para>
///
/// <para>
/// The second is a field Flow cannot positively identify. Once the shell classifies the focused
/// element through UI Automation, <see cref="TargetVerdict.Unresolvable"/> stopped meaning "no text
/// field is focused" and started also meaning "classification failed" — and a field that could not
/// be classified could be a password field. There is no safe record-only fallback for that, so an
/// unidentified target does not start a recording.
/// </para>
/// </remarks>
public static class CaptureAdmissionPolicy
{
    public static CaptureDecision Decide(TargetVerdict verdict, string? targetName)
    {
        var app = string.IsNullOrWhiteSpace(targetName) ? "That app" : targetName!;

        return verdict switch
        {
            TargetVerdict.Ok =>
                new CaptureDecision(TargetAdmission.Dictate, string.Empty),

            TargetVerdict.SecureField =>
                new CaptureDecision(
                    TargetAdmission.Refuse,
                    "Flow does not listen while a password field is focused."),

            TargetVerdict.Elevated =>
                new CaptureDecision(
                    TargetAdmission.RecordOnly,
                    $"{app} runs as administrator, so Windows blocks typing into it. Flow will keep the text for Copy last."),

            // Fail closed. This covers both "nothing editable is focused" and "UI Automation could
            // not classify the element", and the second could be a password field.
            TargetVerdict.Unresolvable =>
                new CaptureDecision(
                    TargetAdmission.Refuse,
                    "Flow could not identify the text field, so it did not start listening."),

            // An unrecognised verdict is treated as unidentified, for the same reason.
            _ => new CaptureDecision(
                TargetAdmission.Refuse,
                "Flow could not identify the text field, so it did not start listening."),
        };
    }
}
