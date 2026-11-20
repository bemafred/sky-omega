// How a target's end is reported when the operating system's record of it is lost (Martin, 2026-11-20): a target
// DrHook killed, whose exit status mscordbi's PAL collected before DrHook's wait read it (finding 87), is reported as
// killed by the signal DrHook sent, marked as such. Nothing else changes: an OS record always wins, and an end unknown
// for another reason stays unknown.

using SkyOmega.DrHook.Engine;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class TargetExitTests
{
    [Fact]
    public void ALostRecord_OfATargetDrHookKilled_IsTheKillDrHookSent()
    {
        TargetExit lost = TargetExit.Unknown(TargetExit.CollectedByAnotherReaper);

        TargetExit reported = lost.OrTheKillDrHookSent(9);

        Assert.Equal(TargetExitKind.Killed, reported.Kind);
        Assert.Equal(9, reported.Signal);
        Assert.True(reported.SentByDrHook);
        Assert.Equal("was killed by signal 9 (SIGKILL), which DrHook sent; another reaper collected the operating system's record of its end first",
            reported.Description);
    }

    [Fact]
    public void ARecordTheOsGave_Wins_OverTheKillDrHookSent()
    {
        TargetExit exited = TargetExit.Exited(0);
        TargetExit bySigterm = TargetExit.Killed(15, coreDumped: false);

        Assert.Same(exited, exited.OrTheKillDrHookSent(9));   // it ended by itself before the kill reached it
        Assert.Same(bySigterm, bySigterm.OrTheKillDrHookSent(9));
        Assert.False(bySigterm.SentByDrHook);
    }

    [Fact]
    public void AnEndUnknownForAnotherReason_OrWithoutAKill_StaysUnknown()
    {
        TargetExit notAChild = TargetExit.Unknown("it is not DrHook's child, so only its own parent can read how it ended");
        TargetExit lost = TargetExit.Unknown(TargetExit.CollectedByAnotherReaper);

        Assert.Same(notAChild, notAChild.OrTheKillDrHookSent(9));
        Assert.Same(lost, lost.OrTheKillDrHookSent(null)); // DrHook sent nothing: someone else's reaper, someone else's end
        Assert.Equal(TargetExitKind.Unknown, lost.OrTheKillDrHookSent(null).Kind);
    }
}
