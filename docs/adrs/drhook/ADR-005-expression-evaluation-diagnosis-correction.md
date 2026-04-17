# ADR-005 — Expression Evaluation Diagnosis Correction: netcoredbg, Not CoreCLR

## Status

**Status:** Accepted — 2026-04-11

## Context

### The Falsification

It was concluded that expression evaluation hangs are caused by CoreCLR's `ICorDebugEval` implementation on macOS ARM64 — a platform-level limitation that "cannot be fixed at the DrHook or netcoredbg layer."

This conclusion is **falsified by a single observation**: JetBrains Rider evaluates expressions successfully on the same hardware (M5 Max, macOS, ARM64), using the same .NET runtime, against the same target process. If `ICorDebugEval` were broken at the CLR level, Rider would also fail. It does not.

Therefore the hang is in **netcoredbg's expression evaluation implementation**, not in CoreCLR.

### How Claude Code Reached the Wrong Conclusion

Claude Code performed what appeared to be a thorough investigation — citing six `dotnet/runtime` issues, analysing netcoredbg's source for platform-conditional code, and constructing a plausible causal chain through thread suspension mechanics, `ICorDebugEval::Abort` regressions, and Apple W^X restrictions.

The analysis exhibits a pattern worth naming: **epistemic depth without epistemic breadth**. Every cited issue is real. The causal chain is internally consistent. But no falsification was attempted. The simplest test — "does expression evaluation work in any other debugger on this platform?" — was never performed or even proposed. The analysis went deep into one hypothesis without checking whether the hypothesis was necessary.

This is a concrete instance of what James ADR-002 calls an epistemic lubricant: the apparent thoroughness of the investigation (six runtime issues, source code analysis, plausible mechanism) created confidence without grounding. The depth was real but the conclusion was unfounded because the critical discriminating test was never run.

### The Three .NET Debugger Architectures

Understanding why the problem is in netcoredbg requires understanding that .NET debugging on macOS involves three completely different debugger implementations, each with its own expression evaluator:

#### JetBrains Rider — `JetBrains.Debugger.Worker.exe` (proprietary)

Rider does not use netcoredbg or vsdbg. The ReSharper backend spawns a separate proprietary debugger process (`JetBrains.Debugger.Worker.exe`) that communicates via JetBrains' internal Rider Protocol. This debugger talks directly to the CoreCLR ICorDebug COM interfaces through dbgshim/libdbgshim.dylib. Expression evaluation uses JetBrains' own Roslyn-based evaluator — they compile expressions via Roslyn, inject the resulting IL into the debuggee, and execute it via `ICorDebugEval` in the context of the stopped thread.

JetBrains ships native ARM64 macOS binaries. Expression evaluation works. This is the falsifying evidence.

#### Microsoft vsdbg (proprietary, closed-source)

VSCode's C# extension uses `vsdbg`, Microsoft's closed-source .NET debugger. It also talks directly to ICorDebug and uses Roslyn for expression compilation. Microsoft ships native ARM64 macOS binaries. vsdbg is license-restricted to Visual Studio, VS Code, and VS for Mac — it cannot be used in DrHook.

#### Samsung netcoredbg (open-source)

netcoredbg implements DAP and GDB/MI for CoreCLR debugging. It also talks to ICorDebug through dbgshim. However:

- **macOS ARM64 is community-supported.** Samsung states: "the MacOS arm64 build (M1) is community supported and may not work as expected, as well as some tests possibly failing." Samsung has no Apple Silicon hardware in CI and ships no official `osx-arm64` release binaries.

- **The expression evaluator is known to be limited.** netcoredbg issue #132 documents that the current expression evaluation implementation is constrained, with community proposals to use Roslyn more fully or DynamicExpresso as alternatives. The evaluator ships with `ManagedPart.dll` plus Roslyn CodeAnalysis and Scripting DLLs, but the evaluation pipeline is less mature than Rider's or vsdbg's.

- **The evaluation hang may be specific to netcoredbg's ARM64 build.** Since there is no official ARM64 CI, subtle issues in native↔managed interop, assembly resolution for ManagedPart.dll, or threading/synchronization in how netcoredbg manages `ICorDebugEval` callbacks on ARM may go undetected.

### The Password Prompt: A Diagnostic Signal

A second observation reinforces the diagnosis. When debugging in Rider, macOS presents a "Developer Tools Access" password dialog. When debugging via DrHook/netcoredbg, no such prompt appears.

This dialog appears when a process calls `task_for_pid()` to obtain a Mach port for another process — the macOS mechanism for cross-process debugging. The `task_for_pid` system call is protected by macOS security and requires the calling process to be code-signed with debug entitlements.

The difference has two possible explanations, both diagnostically significant:

**1. Launch vs. attach semantics.** When DrHook launches the target process as a child via netcoredbg, the parent already has debug rights over its children — no `task_for_pid()` needed. If Rider attaches to an already-running process (or uses a different launch mechanism), it must call `task_for_pid()`, triggering the dialog. This is the benign explanation.

**2. Missing code signature / entitlements.** If the community-built netcoredbg binary lacks proper code-signing with debug entitlements, `task_for_pid()` would fail silently rather than showing a dialog. This would mean netcoredbg is operating with reduced debugging capabilities — potentially explaining why evaluation (which requires deeper process introspection than stepping) fails while basic stepping works.

**Diagnostic action:** Run `codesign -dv /path/to/netcoredbg` and `codesign -d --entitlements - /path/to/netcoredbg` to determine the signing status. If the binary is unsigned or lacks debug entitlements, this is a contributing factor to the evaluation failure.

### Workarounds

Source-level conditional breakpoints, `Debugger.Break()`, variable inspection instead of expression evaluation — are legitimate techniques independent of the root cause diagnosis. They remain valid as fallback patterns. The error was in the causal attribution, not the mitigation strategy.

The `dotnet/runtime` issues are also real issues. Thread suspension on non-Windows platforms, the `ICorDebugEval::Abort` regression, and Apple's W^X restrictions are genuine constraints. They may contribute to making expression evaluation harder to implement correctly on macOS ARM64. But "harder to implement" is not "impossible at the platform level" — as Rider demonstrates.

### Diagnostic Plan

The following manual tests will isolate whether the problem is in netcoredbg's evaluator, its ARM64 build, its code signing, or its interaction with the DAP protocol:

#### 1. netcoredbg CLI evaluation test

Bypass DAP entirely. Run netcoredbg in CLI mode against a simple target:

```bash
netcoredbg --interpreter=cli -- dotnet run --project /path/to/test
```

At a breakpoint, attempt:

```
print sum
print sum + 10
print i > 2
```

If evaluation hangs in CLI mode, the problem is in netcoredbg's eval machinery independent of DAP. If it works, the problem is in how DrHook's DapClient interacts with netcoredbg's DAP evaluate response.

#### 2. Code signing inspection

```bash
codesign -dv /path/to/netcoredbg
codesign -d --entitlements - /path/to/netcoredbg
```

Compare with Rider's debugger worker:

```bash
codesign -dv /Applications/Rider.app/Contents/lib/ReSharperHost/macos-arm64/dotnet/dotnet
```

#### 3. DAP evaluate with timeout

Send a raw DAP evaluate request to netcoredbg with a short timeout and capture the exact failure mode — does it hang (no response), return an error, or crash?

#### 4. Context parameter test

ADR-005 proposed changing from `"watch"` to `"repl"` context before the hang was discovered. Test both contexts in CLI mode to determine if context affects behavior.

#### 5. vsdbg comparison (if license permits)

If VSCode with the C# extension is available, test expression evaluation against the same target. vsdbg is a second independent implementation using ICorDebugEval — if it works, the CLR-level hypothesis is definitively falsified via a second data point.

### Impact on DrHook Architecture

The corrected diagnosis changes the strategic picture:

| ADR-006 conclusion | Corrected conclusion |
|---|---|
| ICorDebugEval is broken on macOS ARM64 | netcoredbg's eval implementation has issues on ARM64 |
| Cannot be fixed at DrHook/netcoredbg layer | Can potentially be fixed by fixing netcoredbg, using a different build, or replacing the eval path |
| DrHook.Engine must implement its own eval | DrHook.Engine should implement its own eval (for sovereignty), but this is a design choice, not a forced constraint |
| Expression evaluation is fundamentally unavailable | Expression evaluation works on this platform (Rider proves it) |
| Workarounds are the only path | Workarounds are one path; fixing netcoredbg or going through dbgshim directly are others |

### Impact on ADR-002

ADR-002's amendment (2026-04-06) states the evaluation was "removed" due to the hang. The amendment's factual observation (evaluation hangs) is correct; its implied attribution to a platform limitation (via cross-reference to ADR-006) is not. When expression evaluation is restored, ADR-002's original design is the specification to implement against.

### Preserved Workarounds

The following patterns from ADR-006 remain valid as fallback techniques, independent of the root cause:

- **Source-level conditional breakpoints** (if-statements with unconditional breakpoints inside) — more expressive than DAP conditions and platform-independent
- **`Debugger.Break()`** — reliable programmatic breakpoint, no eval dependency
- **Variable inspection via `drhook_step_vars`** — uses scopes/variables DAP path, confirmed working

These are not workarounds for a platform deficiency; they are alternative debugging patterns with their own advantages.

## Consequences

### Positive

- Corrects a false attribution that would have permanently foreclosed expression evaluation on macOS ARM64
- Opens diagnostic and fix paths that ADR-006 ruled out
- Demonstrates the Omega Commandment in action: a single falsifying observation (Rider works) overturns an internally-consistent but ungrounded analysis
- Provides a concrete diagnostic plan with testable predictions
- Preserves the valid workarounds from ADR-006

### Epistemic

- Documents a failure mode of AI-assisted analysis: depth without breadth, plausibility without falsification
- The six `dotnet/runtime` issues Claude Code cited are real but were assembled into a causal narrative that was never tested against the simplest discriminating experiment
- This is a Linguistic Phase Signature instance (James ADR-002): the thoroughness of the investigation functioned as an epistemic lubricant, creating confidence in a conclusion that was never empirically grounded

### Trade-offs

- Expression evaluation remains non-functional until the diagnostic plan is executed and the actual root cause in netcoredbg is identified
- The corrected diagnosis does not itself fix the problem — it enables the right investigation
- Some of ADR-006's cited issues may be real contributing factors (thread suspension, abort regression) even though they are not the root cause; these should not be entirely dismissed

## References

- [ADR-002](ADR-002-expression-evaluation.md) — Original expression evaluation specification; amendment factually correct, attribution needs revision
- [Samsung/netcoredbg#132](https://github.com/Samsung/netcoredbg/issues/132) — "Better evaluate expression ideas" — documents known evaluator limitations
- [Samsung/netcoredbg README](https://github.com/Samsung/netcoredbg) — macOS ARM64 community-supported status
- [JetBrains Rider architecture](https://www.codemag.com/article/1811091/Building-a-.NET-IDE-with-JetBrains-Rider) — Debugger runs as separate process via Rider Protocol
- James ADR-002 — Linguistic Phase Signatures and Epistemic Lubricants
