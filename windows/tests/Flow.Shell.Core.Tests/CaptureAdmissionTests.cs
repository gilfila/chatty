using Flow.Core.Abstractions;
using Flow.Core.Insertion;
using Flow.Shell.Core;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// Press-edge admission. Plan invariant 7: Flow never asks for administrator privileges merely to
/// paste — it declines, says so, and keeps the text.
/// </summary>
public sealed class CaptureAdmissionTests
{
    [Fact]
    public void ANormalEditableField_DictatesWithNothingToSay()
    {
        var decision = CaptureAdmissionPolicy.Decide(TargetVerdict.Ok, "Notepad");

        Assert.Equal(TargetAdmission.Dictate, decision.Admission);
        Assert.Equal(string.Empty, decision.Reason);
    }

    [Fact]
    public void APasswordField_RefusesToListenAtAll()
    {
        var decision = CaptureAdmissionPolicy.Decide(TargetVerdict.SecureField, "Chrome");

        Assert.Equal(TargetAdmission.Refuse, decision.Admission);
        Assert.Contains("password", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAdministratorWindow_StillDictatesButPromisesCopyLastUpFront()
    {
        var decision = CaptureAdmissionPolicy.Decide(TargetVerdict.Elevated, "Registry Editor");

        // The safety answer was already settled by TargetGuard. What matters here is that the user
        // is not left to discover it after speaking a paragraph.
        Assert.Equal(TargetAdmission.RecordOnly, decision.Admission);
        Assert.Contains("Registry Editor", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("Copy last", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnidentifiedTarget_RefusesToListenAtAll()
    {
        // Locked product decision: once the shell classifies the focused element through UI
        // Automation, Unresolvable also covers "classification failed" — which could be a password
        // field. There is no safe record-only fallback for that.
        var decision = CaptureAdmissionPolicy.Decide(TargetVerdict.Unresolvable, null);

        Assert.Equal(TargetAdmission.Refuse, decision.Admission);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void AnUnrecognisedVerdict_AlsoRefuses()
    {
        var decision = CaptureAdmissionPolicy.Decide((TargetVerdict)999, "Some App");

        Assert.Equal(TargetAdmission.Refuse, decision.Admission);
    }

    [Fact]
    public void AnUnnamedApp_NeverRendersAnEmptyGapInTheSentence()
    {
        foreach (var name in new[] { null, "", "   " })
        {
            var decision = CaptureAdmissionPolicy.Decide(TargetVerdict.Elevated, name);

            Assert.StartsWith("That app", decision.Reason, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(TargetVerdict.SecureField)]
    [InlineData(TargetVerdict.Elevated)]
    [InlineData(TargetVerdict.Unresolvable)]
    public void EverySomethingIsWrongVerdict_ExplainsItselfBeforeTheUserSpeaks(TargetVerdict verdict)
    {
        var decision = CaptureAdmissionPolicy.Decide(verdict, "Some App");

        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void FlowOnlyEverRecordsOverAPositivelyIdentifiedField()
    {
        // The safety property, stated as a sweep: anything Flow will listen to must have been
        // classified as either OK or a known-elevated target. Everything else refuses.
        foreach (var verdict in Enum.GetValues<TargetVerdict>())
        {
            var decision = CaptureAdmissionPolicy.Decide(verdict, "Some App");

            if (decision.Admission != TargetAdmission.Refuse)
            {
                Assert.Contains(verdict, new[] { TargetVerdict.Ok, TargetVerdict.Elevated });
            }
        }
    }

    [Fact]
    public void NeitherRefusalEverPromisesToKeepText()
    {
        // A refusal that mentions Copy last would be a lie — nothing is recorded at all.
        foreach (var verdict in new[] { TargetVerdict.SecureField, TargetVerdict.Unresolvable })
        {
            var decision = CaptureAdmissionPolicy.Decide(verdict, "Some App");

            Assert.Equal(TargetAdmission.Refuse, decision.Admission);
            Assert.DoesNotContain("Copy last", decision.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The admission decision must agree with what the paste-time guard will actually do, or the
    /// panel promises something the inserter then contradicts.
    /// </summary>
    [Fact]
    public void AdmissionAgreesWithTheGuardItIsLayeredOn()
    {
        var elevated = new TargetDescriptor(
            WindowHandle: 1, ProcessId: 10, ThreadId: 100,
            IsElevated: true, IsSecureField: false, ProcessName: "Registry Editor");

        var verdict = TargetGuard.EvaluateForCapture(elevated);
        var decision = CaptureAdmissionPolicy.Decide(verdict, elevated.ProcessName);

        Assert.Equal(TargetVerdict.Elevated, verdict);
        Assert.Equal(TargetAdmission.RecordOnly, decision.Admission);

        // And the paste-time guard refuses it too, so nothing is ever typed into it.
        var paste = TargetGuard.EvaluateForPaste(elevated, elevated, stillForeground: true);
        Assert.Equal(TargetVerdict.Elevated, paste);
    }

    [Fact]
    public void APasswordFieldIsRefusedByBothTheAdmissionAndTheGuard()
    {
        var secure = new TargetDescriptor(
            WindowHandle: 2, ProcessId: 20, ThreadId: 200,
            IsElevated: false, IsSecureField: true, ProcessName: "Chrome");

        Assert.Equal(TargetVerdict.SecureField, TargetGuard.EvaluateForCapture(secure));
        Assert.Equal(
            TargetAdmission.Refuse,
            CaptureAdmissionPolicy.Decide(TargetGuard.EvaluateForCapture(secure), secure.ProcessName).Admission);
    }
}
