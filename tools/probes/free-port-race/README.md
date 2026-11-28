# What an HttpListener's start says when its port is taken

One probe, run on 2026-11-24 for the tests' `FreePort` (`tests/Mercury.Tests/Fixtures/FreePort.cs`). Mercury reads
the same codes to say "The port is in use." (`HttpListenerLoop.SaysThePortIsInUse`, `src/Mercury/Sparql/Protocol`).
The probe uses the BCL's `HttpListener` alone, not Mercury. It was run on three machines, and each output is beside
the probe:
- zbook: Ubuntu 26.04, x64, .NET 10.0.14;
- macbook: macOS 26.4.1, arm64, .NET 10.0.15;
- omega-windows: Windows 11 26200, arm64, .NET 10.0.15, Swedish system messages.

The question came from Mercury.Tests on zbook (2026-11-23), where a test endpoint's start failed with "Address already
in use". Each test endpoint took its port by binding port 0, reading the port and closing the socket, then starting
on that port. Between the close and the start, another may take the port.

```bash
dotnet run tools/probes/free-port-race/what-a-taken-port-says.cs
```

Each case below takes a port in one way, then starts an `HttpListener` on `http://localhost:{port}/`. The table gives
the start's `HttpListenerException.ErrorCode`, or "started" when the start succeeded.

| The port taken by | Linux | macOS | Windows |
|---|---|---|---|
| a TCP listener on 127.0.0.1 | 98 | started | 32 |
| a TCP listener on [::1] | started | 48 | 32 |
| a TCP listener on [::], dual mode | 98 | started | 32 |
| a TCP listener on 0.0.0.0 | 98 | started | 32 |
| a connection's own end on 127.0.0.1 | started | started | 32 |
| an HttpListener on `localhost`, in the process | 400 | 400 | 183 |
| an HttpListener on `127.0.0.1`, in the process | started | started | started |
| (a start on port 0) | 400 | 400 | 87 |

- On Linux and macOS the managed listener binds the first address `localhost` resolves to: 127.0.0.1 on zbook, [::1]
  on macbook. Only a socket on an address that overlaps that one takes the port from it. The listener of a process is
  shared by its prefixes, so a second prefix with the same host is refused with 400 ("conflicts with an existing
  registration on the machine"). A prefix with another host on the same port is not refused.
- On Windows any socket on the port takes it from http.sys (ERROR_SHARING_VIOLATION, 32). That includes a connection's
  own end, whose port comes from the range port 0 is chosen from. Another registration of the same prefix is refused
  with ERROR_ALREADY_EXISTS (183). The message is the system's, so it is in the system's language: "Det går inte att
  komma åt filen eftersom den används av en annan process" on omega-windows.
- Port 0 is refused on every platform, so a listener cannot be given port 0 and say which port it got.
- On every platform a refused start disposes the listener, so a start on another port needs a new listener.

The codes that say a port is taken are therefore 98 and 400 on Linux, 48 and 400 on macOS, and 32 and 183 on Windows.
On Linux and macOS, 400 is also the code for a malformed prefix, which the tests never give.
