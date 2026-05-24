// Layer 3 — Legacy VSTest integration target test (finding 62 vocabulary).
//
// One xUnit [Fact] that gives the integration-test layer a real-shaped Legacy
// VSTest testhost to debug. Invocation flow:
//
//   dotnet test <this project> --no-build      (with VSTEST_HOST_DEBUG=1 env var)
//        │
//        └── vstest.console
//                  │
//                  └── testhost.dll (Layer 3 — what DrHook attaches to)
//                          │
//                          └── xUnit adapter executes this [Fact]
//
// testhost is the process where this [Fact] runs and where the breakpoint would
// land. With VSTEST_HOST_DEBUG=1, testhost halts at startup BEFORE the [Fact]
// runs and prints its PID to stdout (undocumented format — captured empirically
// in finding 62). After the integration test attaches via DrHook + Continues,
// testhost runs this [Fact].
//
// The [Fact] sleeps long enough for the integration test to do its substrate-
// validation work (attach + brief observe + Dispose). 30s is generous; the
// integration test typically takes < 2s after attach.

using System;
using System.Threading;
using Xunit;

namespace DrHook.Engine.IntegrationTargets.Vstest;

public sealed class IdleFact
{
    [Fact]
    public void IdleForDebuggerObservation()
    {
        // VSTEST_HOST_DEBUG=1 halted testhost at startup; the integration test
        // attached via DrHook + Continued; now this [Fact] runs. Idle long
        // enough for the integration test to complete its validation.
        Thread.Sleep(TimeSpan.FromSeconds(30));
    }
}
