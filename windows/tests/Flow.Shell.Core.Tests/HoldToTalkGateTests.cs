using Flow.Core.Abstractions;
using Flow.Shell.Core;

namespace Flow.Shell.Core.Tests;

/// <summary>
/// The keyboard-listener contract from the delivery plan: press/release/cancel edges that survive
/// auto-repeat, a lost up edge, and a cancel racing the release.
/// </summary>
public sealed class HoldToTalkGateTests
{
    [Fact]
    public void PressAndRelease_ProducesOnePressedAndOneReleased()
    {
        var gate = new HoldToTalkGate();

        Assert.Equal(TriggerEdge.Pressed, gate.Edge(RawKeyEdge.Down));
        Assert.True(gate.IsHolding);
        Assert.Equal(TriggerEdge.Released, gate.Edge(RawKeyEdge.Up));
        Assert.False(gate.IsHolding);
    }

    [Fact]
    public void AutoRepeat_DoesNotStartASecondSession()
    {
        var gate = new HoldToTalkGate();
        Assert.Equal(TriggerEdge.Pressed, gate.Edge(RawKeyEdge.Down));

        // Windows delivers a continuous run of down edges while a key is held.
        for (var i = 0; i < 50; i++)
        {
            Assert.Null(gate.Edge(RawKeyEdge.Down));
        }

        Assert.True(gate.IsHolding);
        Assert.Equal(TriggerEdge.Released, gate.Edge(RawKeyEdge.Up));
    }

    [Fact]
    public void Cancel_WinsOverTheReleaseThatFollowsIt()
    {
        var gate = new HoldToTalkGate();
        gate.Edge(RawKeyEdge.Down);

        Assert.Equal(TriggerEdge.Cancelled, gate.Abort(CancelReason.UserCancelled));

        // The user does still physically let go. That up edge must not finalize the session.
        Assert.Null(gate.Edge(RawKeyEdge.Up));
        Assert.False(gate.IsHolding);
    }

    [Fact]
    public void Cancel_RecordsWhyForThePanel()
    {
        var gate = new HoldToTalkGate();
        gate.Edge(RawKeyEdge.Down);

        gate.Abort(CancelReason.DesktopLocked);

        Assert.Equal(CancelReason.DesktopLocked, gate.LastCancelReason);
    }

    [Fact]
    public void Cancel_WhenNotHolding_FabricatesNothing()
    {
        var gate = new HoldToTalkGate();

        Assert.Null(gate.Abort(CancelReason.UserCancelled));
        Assert.Null(gate.LastCancelReason);
    }

    [Fact]
    public void StrayUpEdge_WithNoMatchingDown_IsIgnored()
    {
        var gate = new HoldToTalkGate();

        Assert.Null(gate.Edge(RawKeyEdge.Up));
        Assert.False(gate.IsHolding);
    }

    [Fact]
    public void AfterCancel_TheNextPressStartsACleanSession()
    {
        var gate = new HoldToTalkGate();
        gate.Edge(RawKeyEdge.Down);
        gate.Abort(CancelReason.UserCancelled);
        gate.Edge(RawKeyEdge.Up); // swallowed

        Assert.Equal(TriggerEdge.Pressed, gate.Edge(RawKeyEdge.Down));
        Assert.Null(gate.LastCancelReason);
        Assert.Equal(TriggerEdge.Released, gate.Edge(RawKeyEdge.Up));
    }

    [Fact]
    public void LostUpEdge_CancelsRatherThanHoldingForever()
    {
        var gate = new HoldToTalkGate(maxHold: TimeSpan.FromSeconds(10));
        gate.Edge(RawKeyEdge.Down);

        Assert.Null(gate.Tick(TimeSpan.FromSeconds(9)));
        Assert.True(gate.IsHolding);

        // This is what a workstation lock or an evicted hook looks like from here: the up edge
        // simply never arrives.
        Assert.Equal(TriggerEdge.Cancelled, gate.Tick(TimeSpan.FromSeconds(1)));
        Assert.False(gate.IsHolding);
        Assert.Equal(CancelReason.ListenerFailed, gate.LastCancelReason);
    }

    [Fact]
    public void LostUpEdge_ThenTheStrayUpArrivesLate_StillDoesNotFinalize()
    {
        var gate = new HoldToTalkGate(maxHold: TimeSpan.FromSeconds(1));
        gate.Edge(RawKeyEdge.Down);
        gate.Tick(TimeSpan.FromSeconds(2));

        Assert.Null(gate.Edge(RawKeyEdge.Up));
    }

    [Fact]
    public void Watchdog_DoesNotFireWhenIdle()
    {
        var gate = new HoldToTalkGate(maxHold: TimeSpan.FromMilliseconds(1));

        Assert.Null(gate.Tick(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Watchdog_ResetsBetweenPresses()
    {
        var gate = new HoldToTalkGate(maxHold: TimeSpan.FromSeconds(10));

        gate.Edge(RawKeyEdge.Down);
        gate.Tick(TimeSpan.FromSeconds(9));
        gate.Edge(RawKeyEdge.Up);

        gate.Edge(RawKeyEdge.Down);
        Assert.Null(gate.Tick(TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public void NoSequenceOfEdges_EverEmitsTwoPressedWithoutAResolution()
    {
        // Exhaustive over every ordering of a small alphabet: the invariant the consumer relies on
        // is that it never has to debounce anything itself.
        var alphabet = new Func<HoldToTalkGate, TriggerEdge?>[]
        {
            g => g.Edge(RawKeyEdge.Down),
            g => g.Edge(RawKeyEdge.Up),
            g => g.Abort(CancelReason.UserCancelled),
            g => g.Tick(TimeSpan.FromSeconds(30)),
        };

        foreach (var sequence in Sequences(alphabet.Length, length: 6))
        {
            var gate = new HoldToTalkGate(maxHold: TimeSpan.FromSeconds(60));
            var open = false;

            foreach (var step in sequence)
            {
                switch (alphabet[step](gate))
                {
                    case TriggerEdge.Pressed:
                        Assert.False(open, "two Pressed without an intervening Released or Cancelled");
                        open = true;
                        break;
                    case TriggerEdge.Released:
                    case TriggerEdge.Cancelled:
                        Assert.True(open, "a resolution with no open session");
                        open = false;
                        break;
                }
            }
        }
    }

    private static IEnumerable<int[]> Sequences(int symbols, int length)
    {
        var buffer = new int[length];
        var total = (int)Math.Pow(symbols, length);

        for (var n = 0; n < total; n++)
        {
            var value = n;
            for (var i = 0; i < length; i++)
            {
                buffer[i] = value % symbols;
                value /= symbols;
            }

            yield return buffer;
        }
    }
}
