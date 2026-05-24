// Layer 3 — MTP integration target test (finding 61 vocabulary).
//
// One [TestMethod] that gives the integration-test layer a real-shaped MTP exe to
// debug. The signaling mechanism is MTP-native:
//
//   When the integration test launches this exe with `--debug`, MTP prints
//   "Waiting for debugger to attach... Process Id: NNNN, Name: ..." on stdout and
//   blocks until Debugger.IsAttached becomes true. The integration test parses the
//   PID, calls DebugSession.Attach, and MTP then proceeds to run [TestMethod]s.
//
// This is the cleanest MTP integration: no custom READY-PID handshake required;
// MTP's --debug option does it. (Discovered during Phase 2 — the assessment doc
// didn't cite it; documented in finding 61.)
//
// The [TestMethod] body just sleeps long enough for the integration test to
// complete its substrate-validation work (Attach → brief observe → Dispose →
// assert target alive). 30s is generous; the integration test typically takes
// < 1s after attach.

using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DrHook.Engine.IntegrationTargets.Mtp;

[TestClass]
public sealed class IdleTarget
{
    [TestMethod]
    public void IdleForDebuggerObservation()
    {
        // MTP's --debug option handled the wait-for-debugger handshake before
        // this method was invoked. Debugger.IsAttached is true here (or the
        // launcher chose not to use --debug, which still works — test just runs).
        Thread.Sleep(TimeSpan.FromSeconds(30));
    }
}
