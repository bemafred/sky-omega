# Finding 01: .NET Diagnostics IPC Protocol — Spec Survey (Epistemics)

> **Reframing note (2026-05-19, post-[ADR-009](../../../docs/adrs/ADR-009-substrate-dependency-policy.md) amendment cascade):** This document characterizes the wire protocol of `Microsoft.Diagnostics.NETCore.Client`, which became a **substrate-admitted dependency** under ADR-009. It is **reference material** — what the admitted dependency does under the hood, useful as axis-4 walk-back evidence if the engine ever needs to reimplement Layer 1 natively. It is **not** implementation prep for a native Layer 1 rewrite. The substrate work narrows to Layer 3 (native ICorDebug interop) per [ADR-006 amended 2026-05-19](../../../docs/adrs/drhook/ADR-006-drhook-engine.md). Probe 01 (a BCL-only Diagnostic IPC round-trip) is no longer load-bearing for the PoC; the equivalent operation is `DiagnosticsClient.GetProcessInfo()` from the admitted NuGet. The protocol survey below stands as substrate-grade reference for the Diagnostic IPC layer.

**Status:**   Epistemics complete — protocol read end-to-end. Probe 01 pending.
**Date:**     2026-05-18
**Hypothesis under study:** A BCL-only client can connect to the .NET Diagnostic IPC socket of a running `dotnet` process and round-trip a `ProcessInfo` message without `Microsoft.Diagnostics.NETCore.Client`, without netcoredbg, without any NuGet.
**Spec read:** https://github.com/dotnet/diagnostics/blob/main/documentation/design-docs/ipc-protocol.md

## What is now known

### Transport

- **macOS / Linux:** Unix domain socket at `${TMPDIR:-/tmp}/dotnet-diagnostic-{PID}-{disambiguator}-socket`
- **macOS sandboxed apps:** Application Group container directory (out of scope for first probe)
- **Windows:** named pipe `\\.\pipe\dotnet-diagnostic-{PID}` (out of scope for first probe; Unix-first idiom established before Windows fork)
- **Disambiguator:** platform-specific
  - macOS / NetBSD: process start time in seconds since epoch
  - Other Unix (Linux): jiffies from `/proc/$PID/stat`

**Probe 01 strategy:** enumerate by glob `dotnet-diagnostic-{PID}-*-socket`, pick the matching file. Avoids platform-specific disambiguator computation for the first probe. A real implementation will need the disambiguator for collision-safe selection when multiple processes share a PID across container/namespace boundaries; out of scope here.

### Diagnostic Ports (reverse connection — runtime connects to us)

Driven by env var `DOTNET_DiagnosticPorts=<address>[,tag...][;...]`. Tags: `suspend|nosuspend` (default `suspend`), `connect` for outbound. When the runtime acts as a client, it sends a 34-byte advertise message and waits for commands:

```
char[8]   magic = "ADVR_V1\0"     (8 bytes)
GUID      runtimeCookie           (16 bytes, little-endian layout)
uint64    processId               (8 bytes)
uint16    future                  (2 bytes, reserved = 0)
```

Out of scope for probe 01 (we're attaching, not being attached to). Worth noting for the engine design — DrHook.Engine will eventually need to support both directions.

### Wire format — IPC header (20 bytes fixed, little-endian throughout)

```
struct IpcHeader {                 // offset  size
  uint8_t[14]  magic;              //   0      14    "DOTNET_IPC_V1\0" (13 ASCII + null)
  uint16_t     size;               //  14       2    total bytes: header (20) + payload
  uint8_t      command_set;        //  16       1
  uint8_t      command_id;         //  17       1
  uint16_t     reserved;           //  18       2    must be 0x0000
};
```

Every IPC message — request or response — starts with this header. `size` is total including header.

### Command sets

```
CommandSet (uint8_t):
  Dump      = 0x01
  EventPipe = 0x02
  Profiler  = 0x03
  Process   = 0x04
  Server    = 0xFF       // used in responses
```

### Process command set (0x04) — full enumeration

```
ProcessCommandId (uint8_t):
  ProcessInfo            = 0x00     // header-only request → pid + GUID + 3 strings
  ResumeRuntime          = 0x01     // header-only request → generic OK
  ProcessEnvironment     = 0x02     // header-only request → multi-message response
  SetEnvironmentVariable = 0x03     // request: 2 strings; response: int32 HRESULT
  ProcessInfo2           = 0x04     // adds: managedEntrypointAssemblyName, clrProductVersion
  EnablePerfMap          = 0x05     // request: uint32 perfMapType; response: int32 HRESULT
  DisablePerfMap         = 0x06     // header-only request; response: int32 HRESULT
  ApplyStartupHook       = 0x07     // request: 1 string; response: int32 HRESULT
  ProcessInfo3           = 0x08     // adds: uint32 version + runtimeIdentifier
```

Other sets exist (EventPipe / Dump / Profiler) but are not needed for probe 01. Layer 2 (EventPipe parser) will revisit `CollectTracing[1-5]` and `StopTracing`.

### ProcessInfo (0x04 / 0x00) — the probe-01 target

**Request:** header only (20 bytes). `size=20, command_set=0x04, command_id=0x00, reserved=0x0000`.

**Response:** header (with `command_set=0xFF, command_id=0x00` — the Server/OK tag, NOT the original `0x04/0x00`) followed by payload:

```
int64      processId               (8 bytes)
GUID       runtimeCookie           (16 bytes — uint32 + uint16 + uint16 + uint8[8])
string     commandLine             (UTF-16LE, length-prefixed in chars, null-terminated)
string     OS                      (UTF-16LE, length-prefixed in chars, null-terminated)
string     arch                    (UTF-16LE, length-prefixed in chars, null-terminated)
```

**String layout:**
- `uint32 charCount` (little-endian; includes the null terminator)
- `charCount * 2` bytes of UTF-16LE code units, last code unit is `0x0000`
- Empty string = `uint32(0)` followed by zero bytes (no terminator)

### Error responses

```
Header:   magic="DOTNET_IPC_V1\0", size=24, command_set=0xFF, command_id=0xFF, reserved=0x0000
Payload:  int32 hresult   (4 bytes, little-endian)
```

Standard HRESULTs (probe-relevant subset):

```
DS_IPC_E_BAD_ENCODING            = 0x80131384
DS_IPC_E_UNKNOWN_COMMAND         = 0x80131385
DS_IPC_E_UNKNOWN_MAGIC           = 0x80131386
DS_IPC_E_NOTSUPPORTED            = 0x80131515
DS_IPC_E_FAIL                    = 0x80004005
DS_IPC_E_NOT_YET_AVAILABLE       = 0x8013135b
DS_IPC_E_RUNTIME_UNINITIALIZED   = 0x80131371
DS_IPC_E_INVALIDARG              = 0x80070057
DS_IPC_E_INSUFFICIENT_BUFFER     = 0x8007007a
DS_IPC_E_ENVVAR_NOT_FOUND        = 0x800000cb
```

Disambiguating OK vs Error in the response: both use `command_set=0xFF`. Check `command_id` — `0x00` for OK, `0xFF` for Error.

### Generic OK response

Sent in response to header-only commands that don't carry a payload (e.g., `ResumeRuntime`):

```
Header:   magic="DOTNET_IPC_V1\0", size=20, command_set=0xFF, command_id=0x00, reserved=0x0000
Payload:  empty (0 bytes)
```

### Byte-level summary

| Component       | Bytes      | Endianness    | Format                                                    |
|-----------------|------------|---------------|-----------------------------------------------------------|
| IPC header      | 20         | little-endian | magic(14) + size(2) + cmd_set(1) + cmd_id(1) + reserved(2) |
| GUID            | 16         | little-endian | uint32 + uint16 + uint16 + uint8[8]                       |
| int32 / uint32  | 4          | little-endian | signed/unsigned 32-bit                                    |
| int64 / uint64  | 8          | little-endian | signed/unsigned 64-bit                                    |
| Non-empty string| variable   | UTF-16LE      | uint32(charCount) + (charCount × 2 bytes) ending in 0x0000|
| Empty string    | 4          | little-endian | uint32(0), no following bytes                             |

## Surprises (Epistemics adjustments to prior assumptions)

1. **Strings are UTF-16LE, not UTF-8.** Sky Omega's parser idiom is span-of-byte over UTF-8. The Diagnostic IPC client needs UTF-16LE readers — a small but real divergence from Mercury's idiom. Implication for substrate: the engine's protocol-decode layer will live closer to .NET's `Encoding.Unicode` than to Mercury's hand-rolled UTF-8 readers. Acceptable; the runtime substrate is what it is.

2. **String length is in characters (UTF-16 code units), not bytes.** Including the null terminator. Mis-reading this as a byte count would over-read by 2× on the payload. Worth a fixture-replay test as soon as a real capture exists.

3. **Single-use connections per command.** Each IPC command requires a fresh socket connection. The streaming-EventPipe exception is what we use for Layer 2, not Layer 1. The probe must `Connect → Send header → Receive header → Receive payload → Close` — no reuse.

4. **No version field in the header.** Version is encoded in the magic string itself (`DOTNET_IPC_V1\0`). A `V2` runtime would refuse our `V1` magic and return `DS_IPC_E_UNKNOWN_MAGIC`. The 2 reserved bytes are not the version slot.

5. **Responses are tagged with the Server command set (0xFF), not the request's command set.** When we send Process/ProcessInfo (0x04/0x00), we get back Server/OK (0xFF/0x00) or Server/Error (0xFF/0xFF). The caller correlates by call context, not by response tag. Implication: a substrate API that returns "raw responses" needs to know which request it issued — there's no way to pattern-match on the header alone.

6. **Platform-specific disambiguator.** macOS uses epoch seconds, Linux uses jiffies from `/proc/$PID/stat`. Both yield a number, but the semantics differ. Probe 01 sidesteps this with glob enumeration; a real client would compute the correct disambiguator.

7. **Process command set is broader than expected.** `ProcessEnvironment`, `SetEnvironmentVariable`, `EnablePerfMap`, `ApplyStartupHook` — Layer 1 is not just "list processes." It includes runtime-control surfaces we may want to expose downstream (e.g., enabling perfmap for a snapshot).

8. **No `mkfifo`-style server-side socket creation needed.** The runtime owns the socket file; we are strictly a client. macOS sandbox container resolution is the one case where the path discovery becomes non-trivial.

## What is still unknown

1. **Behavior under .NET 10 specifically.** Spec is the protocol-level contract; runtime versions may have implementation quirks. The probe will confirm against a known .NET 10 dotnet process.

2. **macOS sandbox container path discovery.** Application Group container is mentioned; the exact path resolution algorithm for sandboxed processes is not in the spec. Defer until we attach to a sandboxed target.

3. **Disambiguator value on this host.** What does a real socket filename look like in `$TMPDIR`? `ls $TMPDIR/dotnet-diagnostic-*` against a live dotnet process before writing the probe will tell us. The probe's glob match should accept whatever's there.

4. **Behavior when multiple sockets match the glob.** Edge case (PID reuse + leftover socket file) — first probe will accept the first match and document the assumption.

5. **BCL `Socket(AddressFamily.Unix, ...)` behavior on macOS/ARM64.** Used elsewhere in .NET but not in Sky Omega code paths to date. Risk is low (this is a well-trodden BCL surface) but unverified for our specific use.

6. **ProcessInfo vs ProcessInfo3 — which to target first.** ProcessInfo (0x00) is the minimum surface — smallest response payload, lowest risk of version drift. ProcessInfo3 (0x08) is richer but adds a version field + runtimeIdentifier that we don't yet have a reason to decode. **Decision:** probe 01 targets ProcessInfo (0x00). Probe 02 can target ProcessInfo3 once we know ProcessInfo round-trips.

## Probe 01 — design implied by survey

**Scope:** ProcessInfo (0x04 / 0x00) round-trip against a known-running dotnet process on macOS/ARM64.

**Algorithm:**

1. Take a pid as command-line argument.
2. Enumerate `${TMPDIR:-/tmp}/dotnet-diagnostic-{pid}-*-socket`. Pick first match (document the assumption).
3. Open `Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)`; connect to `UnixDomainSocketEndPoint(path)`.
4. Build a 20-byte ProcessInfo request:
   - magic[0..13]   = `"DOTNET_IPC_V1\0"` (14 bytes; ensure the null terminator is the 14th byte)
   - size[14..15]   = `0x0014` (20, little-endian)
   - command_set    = `0x04`
   - command_id     = `0x00`
   - reserved[18..19] = `0x0000`
5. `Send` the 20 bytes.
6. `Receive` 20 bytes (response header). Validate: magic, `command_set == 0xFF`, branch on `command_id` (`0x00` OK / `0xFF` error). Read `size`.
7. `Receive` `size - 20` bytes of payload.
8. Decode payload:
   - bytes 0..7:   int64 pid
   - bytes 8..23:  16-byte GUID (print as hex)
   - bytes 24..:   three sequential UTF-16LE length-prefixed strings: commandLine, OS, arch
9. Print decoded values; exit 0 on success.

**Capture:** before close, dump the raw response bytes to `poc/drhook-engine/fixtures/01-processinfo-{runtime-version}-{os-arch}-{timestamp}.bin`. This is the first protocol-trace fixture — it grounds all subsequent layer-1 testability.

**Falsification criteria** (each maps to a class of substrate-design lesson):

| Outcome | What we learn |
|---|---|
| Socket path glob finds nothing for a known-live `dotnet` pid | Path-discovery assumption wrong; check `$TMPDIR` resolution, check whether the runtime actually creates the socket on the platform |
| `Connect` throws | BCL `AddressFamily.Unix` gap on macOS/ARM64, OR socket permissions, OR socket file is stale (process died) |
| `Send` succeeds but `Receive` blocks indefinitely | Likely a malformed magic — runtime opened the connection but didn't recognize the request. Cross-check the 14-byte magic literally (`"DOTNET_IPC_V1"` + 0x00) |
| Response magic doesn't match | Endianness or layout misread on the response side |
| Response is `0xFF/0xFF` with HRESULT | Diagnose by HRESULT; `0x80131386 UNKNOWN_MAGIC` means request magic was wrong; `0x80131385 UNKNOWN_COMMAND` means cmd_set/cmd_id wrong; etc. |
| Strings decode garbled | UTF-16LE length-prefix semantics misunderstood (byte count vs char count) |

**Out of scope for probe 01:**
- ProcessInfo2 / ProcessInfo3 — separate probes once Layer 1 confirms ProcessInfo
- EventPipe session control — Layer 2
- Diagnostic Port (reverse-connection) protocol — separate engine surface
- Windows named-pipe equivalent — separate Unix→Windows-port probe later
- Listing all running dotnet processes (process discovery) — `Process.GetProcesses()` on the .NET side is sufficient for probe-01's "attach to a known pid" scope

## References

- Spec: https://github.com/dotnet/diagnostics/blob/main/documentation/design-docs/ipc-protocol.md (read 2026-05-18)
- Companion Microsoft docs (not yet read; queued):
  - `documentation/design-docs/diagnostics-client-library-design.md` — the NuGet typed surface we're replacing
  - `documentation/design-docs/eventpipe-design.md` — Layer 2 input
- Mercury session 2026-05-18 finding `ipc-protocol-spec-surveyed`
- Mercury session 2026-05-17 decision `drhook-engine-poc-direction` — the directional commitment that scoped this Epistemics work

## Probe outcome

*Pending — added after probe 01 runs.*
