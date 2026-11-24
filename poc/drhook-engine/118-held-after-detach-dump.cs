#!/usr/bin/env -S dotnet
#:package Microsoft.Diagnostics.Runtime@4.2.751201
#:property TargetFramework=net10.0
//
// DrHook.Engine probe 118, the dump reader — what the runtime says of each thread of a held target (ADR-016 6.6) ======
//
// Probe 118 records a held target from outside (each thread's Windows state, its managed stack through EventPipe) and
// asks the target's runtime for a dump with the heap. In every held target one thread, the flood's child, was alive and
// waiting (UserRequest) with 0 ms of CPU and no managed frame, and no thread was suspended (finding 118). What it waits
// for, Windows cannot say. The runtime can: each of its Thread objects keeps a state word (Thread::m_State, threads.h),
// whose bits say whether the thread has started, whether the debugger is suspending it (TS_DebugSuspendPending), and
// whether it waits on its debug suspend event (TS_SyncSuspended, Thread::WaitSuspendEventsHelper). After a detach the
// runtime releases the threads it suspended for the debugger by clearing TS_DebugSuspendPending on each thread in its
// thread list and setting its event (Thread::SysResumeFromDebug, release/10.0), so a thread left with the bit set
// after a detach would wait for ever.
//
// This reads, for each dump: each runtime thread's OS and managed ID, its state word (raw, and each bit by its name in
// threads.h, release/10.0), its GC mode (cooperative or preemptive), its lock count, whether the runtime counts it alive,
// and its managed frames. Native frames it cannot read: ClrMD unwinds managed code only.
//
// ClrMD (Microsoft.Diagnostics.Runtime, Microsoft's library for reading .NET dumps) reads the dump through the runtime's
// own data access component, mscordaccore.dll of the dump's runtime version, so it runs where that runtime is: on
// omega-windows, beside the dumps.
//
// Usage: dotnet run --no-cache 118-held-after-detach-dump.cs -- <dump file or directory> ...
// Result codes: 0 every dump was read; 2 a dump could not be read.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Runtime;

// Thread::ThreadState, threads.h (release/10.0).
var bits = new (uint Bit, string Name)[]
{
    (0x00000001, "TS_AbortRequested"), (0x00000002, "TS_SuspensionTrapped"), (0x00000004, "TS_GCSuspendRedirected"),
    (0x00000008, "TS_DebugSuspendPending"), (0x00000010, "TS_GCOnTransitions"), (0x00000020, "TS_LegalToJoin"),
    (0x00000040, "TS_ExecutingOnAltStack"), (0x00000080, "TS_Hijacked"), (0x00000200, "TS_Background"),
    (0x00000400, "TS_Unstarted"), (0x00000800, "TS_Dead"), (0x00001000, "TS_WeOwn"), (0x00002000, "TS_CoInitialized"),
    (0x00004000, "TS_InSTA"), (0x00008000, "TS_InMTA"), (0x00010000, "TS_ReportDead"), (0x00020000, "TS_FullyInitialized"),
    (0x00080000, "TS_SyncSuspended"), (0x00100000, "TS_DebugWillSync"), (0x00200000, "TS_StackCrawlNeeded"),
    (0x01000000, "TS_TPWorkerThread"), (0x02000000, "TS_Interruptible"), (0x04000000, "TS_Interrupted"),
    (0x10000000, "TS_AbortInitiated"), (0x20000000, "TS_Finalized"), (0x40000000, "TS_FailStarted"), (0x80000000, "TS_Detached"),
};

var dumps = new List<string>();
foreach (string a in args)
{
    if (Directory.Exists(a)) dumps.AddRange(Directory.EnumerateFiles(a, "*.dmp").OrderBy(f => f, StringComparer.Ordinal));
    else dumps.Add(a);
}
if (dumps.Count == 0) { Console.WriteLine("FAIL(2): no dump given. Usage: 118-held-after-detach-dump.cs <dump file or directory> ..."); return 2; }

bool allRead = true;
foreach (string dump in dumps)
{
    Console.WriteLine($"dump {dump}");
    try
    {
        using DataTarget target = DataTarget.LoadDump(dump);
        ClrInfo clr = target.ClrVersions.Single();
        Console.WriteLine($"  runtime {clr.Version} ({clr.Flavor}), module {clr.ModuleInfo.FileName}");
        using ClrRuntime runtime = clr.CreateRuntime();
        foreach (ClrThread thread in runtime.Threads)
        {
            uint state = (uint)thread.State;
            string names = string.Join(" ", bits.Where(b => (state & b.Bit) != 0).Select(b => b.Name));
            uint unnamed = state & ~bits.Aggregate(0u, (all, b) => all | b.Bit);
            Console.WriteLine($"  thread os {thread.OSThreadId} managed {thread.ManagedThreadId}: state 0x{state:X8} [{names}{(unnamed != 0 ? $" unnamed 0x{unnamed:X8}" : "")}], {thread.GCMode}, locks {thread.LockCount}, alive {thread.IsAlive}{(thread.IsFinalizer ? ", finalizer" : "")}");
            int shown = 0;
            foreach (ClrStackFrame frame in thread.EnumerateStackTrace())
            {
                if (shown++ == 12) { Console.WriteLine("      ..."); break; }
                Console.WriteLine($"      {frame.Kind} {frame.Method?.Signature ?? frame.FrameName ?? "?"}");
            }
            if (shown == 0) Console.WriteLine("      (no frame)");
        }
    }
    catch (Exception e)
    {
        allRead = false;
        Console.WriteLine($"  not read: {e.GetType().Name}: {e.Message}");
    }
}
Console.WriteLine(allRead ? "RESULT(0): every dump was read" : "RESULT(2): a dump could not be read (see above)");
return allRead ? 0 : 2;
