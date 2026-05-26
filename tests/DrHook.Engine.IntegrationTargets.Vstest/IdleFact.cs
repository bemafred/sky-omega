// Layer 3 — Legacy VSTest integration target test (finding 62 vocabulary; ADR-008 Increment 3 redesign).
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
// LIFECYCLE DISCIPLINE (ADR-008 / finding 67 / Increment 3): finite observable work
// then natural exit. Previously Thread.Sleep(30s) — Layer 1 violator (testhost required
// substrate kill or caller kill to terminate within reasonable test budget). Now: brief
// Thread.Start/Join work (~500 ms) generates a small handful of mscordbi callbacks for
// substrate observation, then the [Fact] returns. xUnit / VSTest test orchestration
// then finishes naturally — testhost reports results to vstest.console, exits cleanly.
// Substrate's Dispose observes the natural exit via Stage 1 SIGTERM and finding 66
// death-detection.

using System;
using System.Threading;
using Xunit;

namespace DrHook.Engine.IntegrationTargets.Vstest;

public sealed class IdleFact
{
    [Fact]
    public void RunBriefObservableWork()
    {
        // VSTEST_HOST_DEBUG=1 halted testhost at startup; the integration test
        // attached via DrHook + Continued; now this [Fact] runs.
        //
        // Brief observable work: 10 Thread.Start/Join × ~50 ms = ~500 ms total.
        // Generates CreateThread + ExitThread mscordbi callbacks per iteration
        // for substrate observation, then this [Fact] returns naturally.
        // xUnit completes test reporting; testhost exits naturally.
        for (int i = 0; i < 10; i++)
        {
            Thread t = new(static () => Thread.Sleep(20)) { IsBackground = true };
            t.Start();
            t.Join();
            Thread.Sleep(30);
        }
    }
}
