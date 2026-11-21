# ADR-065: mercuryd — One Store, Served by One Process That Runs as a Service, on Linux, macOS and Windows

## Status

**Status:** Accepted — 2026-11-20

Drafted by zbook on 2026-11-20, which holds sky-omega's Mercury role while sky-omega is offline (Martin, the same day:
"We will take Sky-omega's role temporarily.").

Accepted by Martin the same day ("ADR-065 approved."): each decision as written, and each question answered as it
proposed.

Revised the same day, after Martin read the first draft: mercuryd is a minimal host, and its code is in libraries that
mercury and mercury-mcp share (D1, D11, D12); `mercury --attach cli` is dropped (D7); code that only one tool uses stays
where it is, for now (Not in this ADR).

## Why

### The intent

Martin, 2026-11-20:

> The intention is to make elite a shared knowledge base, for me, you and omega. That means a dedicated instance of
> Mercury, for that very purpose.

> One process, per store. That's the rule. SPARQL have SERVICE - that's how an MCP can reach it. Or maybe the connect
> option of Mercury.Cli? The intention is to rely on our tailnet.

> We should make http access work, within our tailnet. We consider this environment safe.

> We should make mercury-cli able to attach as well. […] I meant a mercuryd as a dedicated component.

> The cli and MCP as well as the other tools keep their implementations in libraries, with just a minimal host, we keep
> this pattern. No copy/paste implementation, we make sure we don't duplicate code. Mercury's project layers and
> dependencies serve as an example pattern. If needed, we can add a new project/library for shared code.

A shared store needs a process that holds it all the time, starts with the machine, answers over the tailnet, and
closes the store when it is stopped. No Mercury tool does that today.

### What runs today cannot be a service

Read and measured on zbook at `def7c998`, 2026-11-20:

- **`mercury` (the CLI).**
  - Its REPL reads standard input until it ends. A service manager gives a service `/dev/null`, so the CLI starts,
    reads the end of its input and stops.
  - `--no-repl` skips the REPL, and the process then closes the store and exits. It is meant for profilers and CI
    runs after a load or a rebuild.
  - It handles no stop signal. A SIGTERM ends it without closing the store, and the next open replays the log.
  - When the HTTP endpoint cannot start, it prints a warning and runs on without one.
  - It hosts no attach pipe. `MercuryPorts.CliPipeName` exists, but nothing listens on it, so `mercury --attach cli`
    has nothing to reach.
- **`mercury-mcp`.** It has the lifetime a service needs: its stop signals are handled until the store is closed
  ([ADR-058](ADR-058-wal-replay-restores-consistent-store.md) Q7), and it hosts the HTTP endpoint and the attach pipe.
  But it is an MCP server on standard input. **Measured:** started with `/dev/null` as standard input, it exited at
  once, with code 0, because the MCP stdio transport shuts the server down at the end of its input.
- **Both HTTP endpoints listen on `http://localhost:{port}/` only.** The address is fixed in the code: `Program.cs` in
  Mercury.Cli, `HttpServerHostedService` in Mercury.Mcp.
- **SERVICE is refused by every tool.** `SparqlEngine.Query` builds its `QueryExecutor` with no SERVICE executor
  (`SparqlEngine.cs`, line 74), and the executor then throws. **Measured** on zbook's mercury-mcp 1.8.44:
  `SERVICE <http://localhost:1/sparql> { … }` answers "SERVICE requires an ISparqlServiceExecutor (the cutover injects
  HttpSparqlServiceExecutor)". `mercury_query`, the CLI's REPL and the HTTP endpoint all query through
  `SparqlEngine.Query`.

## Context

### What .NET's HttpListener listens on, on Linux

Measured on zbook, 2026-11-20, with a probe that starts a listener for each address and requests it by `127.0.0.1`, by
the tailnet IP and by the host name:

| The listener's address | Where it listens | What it answers |
|---|---|---|
| `localhost` | 127.0.0.1 | requests to `localhost` only; one to `127.0.0.1` gets 404 |
| `+` or `*` | 0.0.0.0, every network | any host name |
| the tailnet IP | that address only | requests to that IP; one to the host name gets 404 |
| a host name | wherever the name resolves on that machine | zbook: 127.0.1.1, from `/etc/hosts` |
| `0.0.0.0` | refused at start: "The request is not supported" | — |

So on Linux the address is where the listener listens, and a request must use one of the listener's host names, or it
gets 404. macOS uses the same managed listener (not measured). Windows uses its kernel HTTP server, http.sys: a
listener address other than `localhost` needs a URL reservation (`netsh http add urlacl`) unless the process runs as an
administrator, and the port needs a firewall rule (not measured).

### Where a named pipe lives

Measured on zbook, 2026-11-20:

- A pipe name that is not a path becomes a Unix socket at `$TMPDIR` (or `/tmp`) + `CoreFxPipe_<name>`.
- **A pipe name that is an absolute path is used as the socket's path.** It is checked against the length a Unix socket
  path may have: 108 characters on Linux. macOS allows 104 (not measured).
- Read, not measured: a macOS LaunchDaemon and a user's ssh shell do not have the same `TMPDIR`, so a socket in the
  temporary directory would not be found by the client.
- Windows pipes are `\\.\pipe\<name>`, one namespace for the machine, with an access list.

### elite

Checked 2026-11-20: tailnet address `100.67.254.98`, name `elite.tail7f8646.ts.net`. On elite itself, `elite` resolves
to 127.0.1.1 and the full name to the tailnet address. The user has `Linger=no`. No port 3030 to 3099 is in use. The
tools are 1.8.44, and the checkout is at `def7c998`.

### What exists to build from

- **`QuadStorePool`**, which refuses a second process on a store (`StoreInUseException`): one process per store is
  already enforced.
- **The .NET Generic Host** (`Microsoft.Extensions.Hosting` 10.0.0), already a dependency of mercury-mcp, and its two
  hosted services, `HttpServerHostedService` and `PipeServerHostedService` (`src/Mercury.Mcp/Services`, 86 lines). They
  move to a library (D12).
- **`PipeServer` and `ReplSession`** in Mercury.Runtime: the REPL over a pipe that mercury-mcp offers today.
- **`FileLogger`**: `mercury.log` in the store's directory, which every Mercury process writes.
- **ADR-058 Q7's stop handling**: SIGINT and SIGTERM registered before the host starts and kept until the store is
  closed.

### How the tools are built today

Read at `def7c998`, 2026-11-20.

Mercury's layers: `Mercury.Abstractions`, which references nothing; `Mercury.Runtime` over it; `Mercury`, the core, BCL
only; the libraries over the core (`Mercury.Pruning`, `Mercury.Solid`, `Mercury.Sparql.Tool`, `Mercury.Turtle.Tool`);
the tools at the top. Packages from outside the BCL come in at the top only: `ModelContextProtocol` and
`Microsoft.Extensions.Hosting`, in mercury-mcp.

- **The pattern holds** for `mercury-sparql` and `mercury-turtle`. Each `Program.cs` reads the options and calls
  `SparqlTool.RunAsync` or `TurtleTool.RunAsync`, and the libraries are tested in Mercury.Tests. `drhook-viz-console`
  and `drhook-viz-tui` are built the same way over `DrHook.Viz`.
- **It does not hold for `mercury`.** All of it, 786 lines, is in `Program.cs`, and no test project references it.
- **Nor for `mercury-mcp`.** Opening the store, the stop handling, the two hosted services and the MCP tools are in the
  tool's project, and Mercury.Mcp.Tests references the tool.

Code that is repeated, and that mercuryd would repeat once more:

| Code | Where | Copies |
|---|---|---|
| Open a store with its log; close it and log the stop | `Mercury.Cli/Program.cs` lines 343–399 and 666–668; `Mercury.Mcp/Program.cs` lines 119–154 and 221–223 | 2 |
| A REPL session over the store | `Mercury.Cli/Program.cs` lines 628–654; `Mercury.Mcp/Program.cs` lines 237–242 | 2 |
| Attach as a client, and the pipe a target name means | `RunAttachSession` and `RunAttachMode`, both in `Mercury.Cli/Program.cs` | 2 |
| The address `http://localhost:{port}/` | `Mercury.Cli/Program.cs`, `HttpServerHostedService`, `MercuryPorts.GetBaseUrl` | 3 |
| The stop signals held until the store is closed (ADR-058 Q7) | `Mercury.Mcp/Program.cs` lines 210–233 | 1 |
| The hosted HTTP and pipe services | `src/Mercury.Mcp/Services` | 1 |
| `LooksLikeSparql`, which refuses a query given where a store path goes | both tools' `Program.cs` | 2 |
| The `--version` line | 7 places in the 4 Mercury tools; 4 in the 3 DrHook tools | 11 |

The servers are already shared: `SparqlHttpServer` and `HttpListenerLoop` in Mercury, one listener lifecycle since
`4f0e95cc`, and `PipeServer` and `PipeClient` in Mercury.Runtime. What is repeated is the code each tool puts around
them.

mercury-mcp and mercuryd are the same process but for one door. Each holds one store and serves it over HTTP and the
attach pipe until it is stopped; mercury-mcp also answers MCP on standard input, and stops when that input ends.

### Which profile

The shared store's profile is chosen when it is made ([ADR-029](ADR-029-store-profiles.md)). The Graph profile is
meant for "facts that change, kept without a clock" ([ADR-064](ADR-064-a-facts-states.md) M1); Cognitive is one mind's
memory, with one writer. The profile is not this ADR's decision: mercuryd serves any of the four.

## Decision

### D1. mercuryd is a component of its own, and a minimal host — Accepted

- A project, `src/Mercury.Daemon`, packed as a global tool whose command is `mercuryd`, as the other Mercury tools
  are.
- Its `Program.cs` reads the options with `Mercury.Hosting`, adds the Windows service support (D8), and runs the store
  host that `Mercury.Hosting` gives (D11). Its help text is its own. Nothing else is in it.
- It serves; it has no REPL and does no load, rebuild or conversion. Those stay in the `mercury` CLI, run while
  mercuryd is stopped, since a store has one process.
- The reason it is not a mode of the CLI: the CLI is an interactive tool with a REPL and a dozen one-off operations,
  and a service has no terminal, starts when the machine does, and stops when its service manager says so. Each is
  simpler alone.

### D2. One mercuryd serves one store — Accepted

- The store is named with `--store <name>` (under `<stores>`, as the CLI's) or given with `-d <path>`.
- `--profile` applies when the store is made, and an existing store keeps its own (ADR-029).
- A second process on the store is refused, as today.

### D3. Its lifetime — Accepted

- **Start:** open the store, start the HTTP endpoint, start the attach socket. If any of them fails, mercuryd says why
  in its log and on standard error and exits with a code other than 0, so that the service manager restarts it. It
  never runs on without a part, as the CLI does without its HTTP endpoint.
- **Run** until it is stopped: by SIGTERM or SIGINT on Linux and macOS, or by the service's stop request on Windows.
- **Stop:** stop taking requests and connections, let the requests in progress finish within a bound, close the store,
  write the stop to the log, and exit 0. The stop signals stay handled until the store is closed (ADR-058 Q7).
- A SIGKILL, or a power cut, ends it at once; the next open replays the log (ADR-058).

### D4. It listens where it is told — Accepted

- `--listen <host>`, which may be given more than once, and `--port <port>`. Without `--listen`, it listens on
  `localhost`.
- Each host is one listener address, so it listens there and answers requests that use that name (Context). On
  elite: `--listen 100.67.254.98 --listen elite.tail7f8646.ts.net`, both on the tailnet address only.
- `+` and `*` are allowed, and they listen on every network the machine has, the local one included.
- `SparqlHttpServer` and `HttpListenerLoop` take one address today; they take several.

### D5. Updates over HTTP are on — Accepted

- The shared store is written by its clients, so the update endpoint, `POST /sparql/update`, is on.
- `--read-only` turns it off.
- No authentication: the tailnet is the boundary (Martin: "We consider this environment safe"). Listening on `+` or
  `*` gives up that boundary, and the help text says so.

### D6. The attach socket — Accepted

- **Linux and macOS:** a Unix socket in the store's directory, `<store>/mercuryd.sock`, named by its absolute path.
  Where it is is then the same for the daemon and for every client, whatever their temporary directory. Whoever may
  read the store's directory may attach.
- If that path is longer than a socket path may be (108 characters on Linux, 104 on macOS), the socket is
  `mercury-<store name>` in the temporary directory instead, and the log says where it is.
- **Windows:** a named pipe, `mercury-<store name>`, whose access list lets the user who runs the service in, and the
  user named in the service's install.
- Over it runs the REPL mercury-mcp's pipe gives today (`ReplSession`).

### D7. The CLI attaches to a store — Accepted

- `mercury --attach --store <name>` (or `-d <path>`) finds the socket from the store. `mercury --attach`, which is
  `--attach mcp`, and `mercury --attach <pipe name>` work as today.
- From another machine: `ssh elite mercury --attach --store <name>`, over the tailnet.
- **`mercury --attach cli` is dropped** (Martin, 2026-11-20). Nothing listens on `mercury-cli` (Why), so
  `MercuryPorts.CliPipeName` goes with it, and so does the CLI tutorial's "Attach to another CLI instance". If a need
  for it shows, it is added and built whole: Martin, "If a need surfaces later, it can be added and implemented fully."

### D8. Each platform's service manager — Accepted

| | Linux | macOS | Windows |
|---|---|---|---|
| Service | a systemd system unit with `User=`, so it starts at boot with nobody logged in; `Restart=on-failure` | a LaunchDaemon with `UserName`, `RunAtLoad` and `KeepAlive` | a Windows service, with restart as its recovery action |
| Stop | SIGTERM, then SIGKILL after `TimeoutStopSec` | SIGTERM, then SIGKILL after `ExitTimeOut` (20 s by default) | the service's stop request; a service that never answers it is killed |
| Code | the stop signals (D3) | the same | Windows service support: `Microsoft.Extensions.Hosting.WindowsServices` |
| Install | a unit file | a plist | a script: the service, the URL reservation, and a firewall rule for the port limited to the tailnet's addresses (100.64.0.0/10) |

- The unit file, the plist and the script are in the repository, with the runbook that installs them.
- A tailnet address that is not up yet when the machine starts makes the start fail (D3), and the service manager
  starts it again.

### D9. Its logs — Accepted

- `mercury.log` in the store's directory, as every Mercury process: each start and stop, each store opened, and every
  warning and error.
- Standard output and standard error go to the service manager's log: journald, the plist's `StandardOutPath` and
  `StandardErrorPath`, or the Windows event log through the hosting package.

### D10. SERVICE works from every tool — Accepted

- `SparqlEngine.Query` gives each query an `HttpSparqlServiceExecutor`. A session's own mercury-mcp then reads the
  shared store with `SERVICE <http://elite.tail7f8646.ts.net:3032/sparql> { … }`: Martin's "that's how an MCP can
  reach it".
- SERVICE reads; it never writes to the remote store. A client writes with `POST /sparql/update`.
- Every endpoint that takes queries can then be made to send requests to other addresses. On the tailnet this is
  accepted with D5.
- This is not mercuryd's code, but without it no MCP reaches mercuryd.

### D11. The layers — Accepted

The rule is Martin's (Why): each tool is a minimal host over libraries, and no code is copied. The split:

```
Mercury.Abstractions
└─ Mercury.Runtime        + where a store's attach socket or pipe is (D6); + the addresses to listen on (D4)
   └─ Mercury             the core, BCL only: SparqlHttpServer on several addresses (D4); SERVICE (D10)
      ├─ Mercury.Pruning
      └─ Mercury.Serving       new, BCL only; references Mercury.Pruning too
         ├─ mercury            Mercury.Cli
         └─ Mercury.Hosting    new; + Microsoft.Extensions.Hosting
            ├─ mercury-mcp     Mercury.Mcp; + ModelContextProtocol
            └─ mercuryd        Mercury.Daemon; + Microsoft.Extensions.Hosting.WindowsServices
```

| Project | What it holds | Used by |
|---|---|---|
| `Mercury.Runtime`, more in it | Where a store's attach socket or pipe is (D6), for the daemon and for the client. The listener addresses from `--listen` and `--port` (D4). Beside `MercuryPorts`, `PipeServer` and `PipeClient`. The version line, `ToolVersion`: here, not in `Mercury.Serving`, so that mercury-sparql and mercury-turtle use it too without a reference they have no other use for (moved here while Step 2 was done). | every tool |
| `Mercury.Serving`, new | What a process that holds one store does: open the store with its log, close it and log the stop; the REPL session over it, with the commands each tool offers, prune's options among them; the options the tools share (`--store`, `-d`, `--profile`, `--listen`, `--port`); `LooksLikeSparql`. | mercury, mercury-mcp, mercuryd |
| `Mercury.Hosting`, new | What a process that holds one store until it is stopped does: the hosted HTTP and attach services; the stop signals held until the store is closed (ADR-058 Q7); the store host both tools run; mercuryd's options. | mercury-mcp, mercuryd |

- `Mercury.Serving` cannot be in `Mercury.Runtime`, which is below the core. It is not in the core, which would then
  carry the tools' names and what they print.
- The CLI runs no Generic Host, so the hosting package stays out of it. That is why there are two libraries, not one.
- The Windows service package is only in mercuryd.
- Each tool keeps its own words: its help, and what it says when its store is in use.
- Tests: `Mercury.Serving` in Mercury.Tests, as `Mercury.Sparql.Tool` is; `Mercury.Hosting` and mercuryd in a new
  `tests/Mercury.Hosting.Tests`.

### D12. The shared code moves first, and mercury and mercury-mcp behave as before — Accepted

- Before mercuryd is written, the code in Context's table moves to where D11 puts it, and mercury and mercury-mcp are
  built on it.
- They keep their options, their defaults (updates over HTTP on in the CLI; off in mercury-mcp unless
  `--enable-http-updates`), their output and their log lines. The one change is D7's: `--attach cli` is gone.
- What moves gets tests in its new library. The CLI's part of it has none today.
- mercuryd then has no code of its own but its host (D1).

## Questions

Each was answered as it proposed, when Martin accepted this ADR on 2026-11-20.

- **Q1. The port.** Decided: 3032, as `MercuryPorts.Daemon`, mercuryd's default (3030 is mercury-mcp's, 3031 the
  CLI's).
- **Q2. Updates on by default (D5)?** Decided: yes, with `--read-only`.
- **Q3. A REPL over HTTP,** `mercury --connect <url>`, so that attaching from another machine needs no ssh? Decided:
  later, in its own ADR. ssh over the tailnet does it now (D7).
- **Q4. elite's store.** Decided: named `shared`, Graph profile, made by mercuryd's first start.
- **Q5. The release.** A new tool is a new version (semver). Decided: the next minor version, cut when Martin says.
- **Q6. Should systemd know when mercuryd is ready?** A unit of `Type=notify`, through
  `Microsoft.Extensions.Hosting.Systemd`, is "started" only once the store is open and the listeners are up, so
  `systemctl start` waits for that and reports a failed open. A unit of `Type=simple` is "started" when the process is,
  and `Restart=on-failure` still restarts it. Decided: `Type=simple`, and no package, until a need shows.

## Steps

The order follows the dependencies (ADR-064's rule).

1. **Martin's review** of this text. Done: accepted 2026-11-20.
2. **The shared code moves** (D11, D12): `Mercury.Serving`, `Mercury.Hosting` and what `Mercury.Runtime` gains;
   mercury and mercury-mcp built on them and behaving as before; `--attach cli` dropped (D7); with tests.
   Done on zbook, 2026-11-20: 59 tests in Mercury.Tests (`Serving/`) and 3 in `tests/Mercury.Hosting.Tests`, each
   of seven mutations of the moved code failed by one of them; Mercury.Tests 6546 passed, 0 failed, 155 skipped (the
   W3C syntax suites' skips). One change seen: `:store` and mercury-mcp's pipe welcome name the store by its full
   path when a relative one is given. The stop signals are tested in Step 4, on mercuryd's own process.
3. **Several listen addresses** in `HttpListenerLoop` and `SparqlHttpServer` (D4), with tests.
   Done on zbook, 2026-11-20:
   - The addresses are one listener's prefixes, one port and one accept loop. A duplicate is listened on once, and
     `SparqlHttpServer.BaseUri` became `BaseUris`.
   - The service description names the endpoint the request reached. A server on several addresses has no one base
     URI, and `+` or `*` is no IRI.
   - A start that fails on any address fails whole. The managed listener (Linux, macOS) unbinds what it had bound and
     closes itself. The loop now makes its stop state only after a start: a stop of that closed listener bound the
     failed address again and threw, which would have hidden the start's exception (D3). Both servers had made it
     before the start since before `4f0e95c` (sky-omega read `4f0e95c^`), so the fix covers both.
   - `MercuryPorts.GetBaseUrl(host, port)` and `GetEndpoint(host, port)` form an address, an IPv6 one in brackets.
   - `SolidServer` keeps one address, since a resource's IRI is built from it.
   - Tests: `SparqlHttpServerAddressesTests`, each failing under a mutation that undoes what it tests (five).
     - On Windows a listener on `127.0.0.1` needs a URL reservation or an administrator; without either, those tests
       are skipped.
     - The failed start is tested on Linux and macOS only: Windows' http.sys registers an address that isn't the
       machine's without binding it.
4. **`src/Mercury.Daemon`** (D1 to D3, D5, D6, D9), with tests in `tests/Mercury.Hosting.Tests`: a start that serves
   queries and updates over HTTP, an attach over the socket, a SIGTERM that closes the store and writes the stop to
   the log, a start that fails exits with a code other than 0, and a second process on the store is refused. Each test
   fails under a mutation that undoes what it tests.
   Done on zbook, 2026-11-21:
   - **The tool.** `mercuryd` reads its options with `DaemonArguments` (Mercury.Hosting) and serves its store with
     `StoreHost`. Its default port is 3032, `MercuryPorts.Daemon` (Q1).
     - A store must be named, with `--store` or `-d` (D2). A service that made a store nobody named would serve the
       wrong one.
     - It prints `Ready.` once its endpoints have started, and `Stopping: …` when a stop begins.
   - **The attach socket** (D6) is `MercuryPorts.DaemonPipeName`, for the daemon and its clients.
     - A socket's path is held with its terminating zero. On Linux a path of 107 bytes is bound and one of 108 is
       refused (measured); macOS holds 103 bytes and 104 with the zero.
     - A socket file left by a killed process is replaced at the next start (measured: .NET deletes it and binds again).
       A directory at that path fails the start.
     - Connecting needs write permission on the socket, and the umask sets it (`srwxrwxr-x` on zbook). So D6's
       "whoever may read the store's directory may attach" holds for the user and the group, not for others.
     - Not handled: two stores of the same name, when the socket falls back to the store's name (on Windows always).
       The second process then takes the first one's name: on Linux and macOS it deletes the first one's socket, and
       on Windows it adds instances to the first one's pipe. Step 9 decides Windows'.
   - **D3's start.** A part that cannot start fails the start whole. mercuryd then says why on standard error, and
     once the store is open in its log too, closes the store, and exits 1. Found on the way:
     - `PipeServer` made its pipe in its accept loop, which took the error for a connection's and made the pipe again
       at once. A pipe that could not be made spun for ever inside `Start`. The first pipe is now made in `Start`.
     - `StoreHost.RunAsync` let a failed host's exception out of Main: the store was not closed, and the process ended
       by abort. It now returns the exit code.
     - A store that cannot be opened is said, and not let out of Main, for the same reason.
   - **D3's stop.** The requests in progress finish before the store is closed. Found on the way:
     - `HttpListener.Stop` ends a response in progress (measured: the write after it throws, and the client gets an
       empty response).
     - Neither server waited for its requests or its sessions, so the store was closed while they could still read it,
       and `QuadStore.Dispose` takes no lock.
     - Now the HTTP loop stops taking requests, answers 503 to one that comes meanwhile, waits for the responses in
       progress, and only then stops the listener. The pipe waits for each session's command.
     - The bound is the service manager's (D8). The Generic Host waits for a stop past its own shutdown timeout
       (measured), and a service manager that stops waiting kills the process, which the next open recovers from
       (ADR-058).
   - **mercury-mcp** runs the same host, so it starts and stops the same way.
     - A port in use now exits 1 with the store closed.
     - Its stop waits for the requests in progress. Its MCP tool calls in progress at a stop belong to the MCP server,
       and are not waited for here.
     - Its log gains two lines at each start: where it serves SPARQL, and its pipe.
   - **Tests.**
     - `MercurydTests` runs mercuryd as a process with no input, as a service manager does:
       - queries and updates over HTTP, and none with `--read-only`;
       - each `--listen` address;
       - an attach over the store's socket;
       - SIGTERM and SIGINT, each closing the store, writing the stop and exiting 0;
       - three failed starts (the port in use, an address that is not the machine's, a file where the store goes),
         each exiting 1;
       - a second process refused while the first serves on;
       - a start after a kill, serving what was written;
       - the profile, applied when the store is made.
     - `DaemonArgumentsTests` has 8 cases.
     - In Mercury.Tests: `HttpListenerLoopStopTests`, `PipeServerStartAndStopTests` (2), `DaemonPipeNameTests` (3), and
       `--listen` in `StoreArgumentsTests`.
     - 18 mutations, each undoing one of these, each failed by its test. Under the first run of one of them (the listener
       stopped before the responses), the HTTP stop test hung rather than failed: its disposal waited for the
       response it held. Both stop tests now let it go in a `finally`.
   - **Not refuted:** a SIGTERM while the store is being closed (ADR-058 Q7's `StopSignals`). While the host runs, its
     console lifetime handles SIGTERM and SIGINT too, so a signal test passes without `StopSignals`. The window it
     covers is the store's close after the host has stopped, and no test holds that open.
   - **Windows:** the signals, an address that is not the machine's and a second address are not tested there. The
     pipe's access list and the stop request are Step 9.
5. **The CLI attaches by store** (D7).
   Done on zbook, 2026-11-21:
   - **The attach.** `mercury --attach` with a store given (`--store <name>`, `-d <path>`, or the path as the
     argument) attaches to the mercuryd of that store, on the pipe that `MercuryPorts.DaemonPipeName` names for it,
     the one mercuryd makes. `AttachTarget` (Mercury.Serving) is what is attached to: `Named`, mercury-mcp or a pipe,
     and `DaemonOf`, a store's mercuryd. The store is not opened.
     - `--attach` reads a pipe's name only when the next argument is not an option. Before, `--attach --store kb`
       attached to mercury-mcp: an option after `--attach` made the target `mcp`, and the store was read and ignored.
     - A pipe's name and a store together are refused, and the CLI exits 1.
     - `--attach` alone and `--attach <pipe>` work as before. `--attach <store's directory>` is a pipe's name, as
       before: a store's directory goes with `-d`, or before `--attach`.
   - **Found on the way: a store made in the current directory.** `MercuryPaths.Store` asked .NET for the user's data
     directory, and .NET gives an empty path for a special folder that does not exist yet. A user that had never kept
     data there got `SkyOmega/stores/<name>` in the current directory: measured with `XDG_DATA_HOME` naming a
     directory not made, and with it unset and no `~/.local/share`. The account a service runs as is such a user, and
     a systemd service's directory is `/` (Step 7). Every tool that names a store had it. The path is now taken
     without looking (`SpecialFolderOption.DoNotVerify`), and the store's directory is made with its parents.
   - **A stop while a CLI is attached** (measured): mercuryd ends the session at once and exits 0. The CLI waits for
     the user's next line, and learns at it that mercuryd has gone: it says `Error: Broken pipe` and exits 1. That is
     the client as before, attached to mercury-mcp too. A client that watched the pipe while it waits would say so at
     once; not done here.
   - **Not handled:**
     - `--attach --store` with no name attaches to mercury-mcp: a missing value is ignored (`StoreArguments`).
     - `--attach --store <name>` makes the store's directory when there is none, as every `--store` does. `-d` makes
       nothing.
     - The fallback socket name (D6) is in the temporary directory, so the CLI finds it only with mercuryd's
       temporary directory. A systemd unit with `PrivateTmp=` has one of its own (Step 7); on macOS a user's `TMPDIR`
       is the user's, and a LaunchDaemon's may not be (Step 8).
     - A store reached through a symbolic link of another name: the socket in the store's directory is the same
       file, but the fallback name is the link's.
     - The REPL's `:attach` takes a pipe's name, not a store.
   - **Tests.** `AttachToMercurydTests` runs mercury and mercuryd as processes, with the test's own stores' directory,
     not made beforehand, and its own temporary directory, so no mercury-mcp of the machine's answers:
     - five ways of giving the store (`--attach --store`, `--store … --attach`, `-a --store`, `--attach -d`, the path
       before `--attach`), each attached to its mercuryd;
     - mercuryd's socket attached by its name, as a pipe before;
     - `--attach` alone, on mercury-mcp's pipe;
     - a store nothing serves, said, and not made;
     - a pipe's name and a store, refused;
     - an attached CLI that does not hold mercuryd's stop.
     - 10 mutations, each undoing one of these, each failed by its test: among them `MercuryPaths.Store` looking for
       the data directory again, and a pipe session that a stop does not end.
6. **SERVICE** in `SparqlEngine.Query` (D10), with a test that queries one endpoint from another.
7. **Linux:** the unit file and the runbook; mercuryd on elite with the store of Q4, installed by Martin; queried and
   written from zbook and macbook over the tailnet.
8. **macOS** on macbook: the plist; the listener and the socket measured there, and whether the macOS firewall lets a
   LaunchDaemon take connections.
9. **Windows** on omega-windows, when omega is back: the service, the URL reservation, the firewall rule, the pipe's
   access list.
10. **Documents:** MERCURY.md, CLAUDE.md's tool table and solution structure, README, the CLI tutorial,
    `install-tools.sh` and `install-tools.ps1`.

## Not in this ADR

- Authentication and TLS. The tailnet is the boundary (D5).
- mercury-mcp as a client of mercuryd, rather than the holder of its own store.
- Backups of the shared store: a dump and a reload.
- The W3C SPARQL suites run on a Graph store: a test of the profile, not of mercuryd. Asked for the same day.
- More than one store per process (D2).
- Code that only one tool uses, moved into a library of its own: the CLI's load, conversion and rebuild progress and
  metrics (about 250 lines of `Mercury.Cli/Program.cs`), `MercuryTools` in mercury-mcp, `EngineSteppingSession` in
  drhook-mcp (1631 lines), and the DrHook tools' version lines. The same rule applies to them, and Martin agreed on
  2026-11-20 that this is done separately.
- `DrHook.Capture`, which opens its Graph store as a bare `QuadStore`, with no pool and no log. Whether it opens its
  store through `Mercury.Serving` is its own question.

## References

- [ADR-029](ADR-029-store-profiles.md): the four store profiles.
- [ADR-058](ADR-058-wal-replay-restores-consistent-store.md): the log replayed at open; Q7, the stop signals.
- [ADR-064](ADR-064-a-facts-states.md): the Graph profile's model.
- [ADR-047](ADR-047-default-path-cutover.md) and [ADR-048](ADR-048-service-federation-efficiency.md): SERVICE in the
  query engine.
- [Test machines runbook](../../operations/runbook-test-machines.md), [Linux .NET runbook](../../operations/runbook-linux-dotnet-runtime.md).
