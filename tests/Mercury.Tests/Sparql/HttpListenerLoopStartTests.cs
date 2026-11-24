// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using SkyOmega.Mercury.Sparql.Protocol;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A refused start of the HTTP endpoint says why. When the port is in use, Mercury says so: the system says it with a
/// code of its platform, and on Windows, when a socket holds the port, as a file that another process uses, in the
/// system's language (<c>tools/probes/free-port-race</c>). Any other refusal is said in the system's words.
/// </summary>
public sealed class HttpListenerLoopStartTests
{
    [Fact]
    public void AStart_OnAPortThatASocketHolds_SaysThePortIsInUse_WithTheSystemsCode()
    {
        // The listener of Linux and macOS listens on the first address localhost resolves to (127.0.0.1 on zbook, [::1]
        // on macOS); on Windows a socket on any address holds the port from http.sys. The system names the port, and
        // the socket holds it from then on.
        var holder = new TcpListener(Dns.GetHostAddresses("localhost")[0], 0);
        holder.Start();
        try
        {
            var port = ((IPEndPoint)holder.LocalEndpoint).Port;
            using var loop = new HttpListenerLoop([$"http://localhost:{port}/"], (_, _) => Task.FromResult<byte[]>([]));

            var refused = Assert.Throws<HttpListenerException>(loop.Start);

            Assert.Equal("The port is in use.", refused.Message);
            Assert.Equal(OperatingSystem.IsWindows() ? 32 : OperatingSystem.IsMacOS() ? 48 : 98, refused.ErrorCode);
        }
        finally
        {
            holder.Stop();
        }
    }

    [Fact]
    public void AStart_OnAUriThatAnotherListenerHas_SaysThePortIsInUse_OnWindows_AndIsSaidInTheSystemsWordsElsewhere()
    {
        // http.sys holds the URIs of every process, and says ERROR_ALREADY_EXISTS (183). On Linux and macOS another
        // listener of this process is refused with 400, the code of a URI the listener cannot listen on too.
        using var first = FreePort.Start(
            port => new HttpListenerLoop([$"http://localhost:{port}/"], (_, _) => Task.FromResult<byte[]>([])),
            loop => loop.Start(), out var port);
        using var second = new HttpListenerLoop([$"http://localhost:{port}/"], (_, _) => Task.FromResult<byte[]>([]));

        var refused = Assert.Throws<HttpListenerException>(second.Start);

        Assert.Equal(OperatingSystem.IsWindows() ? 183 : 400, refused.ErrorCode);
        Assert.Equal(OperatingSystem.IsWindows(), refused.Message == "The port is in use.");
    }

    [Fact]
    public void AStart_RefusedForAnotherReason_IsSaidInTheSystemsWords()
    {
        // Port 0, which a listener cannot be given: 87 on Windows, and on Linux and macOS 400, the code of another
        // listener of this process on the URI too.
        using var loop = new HttpListenerLoop(["http://localhost:0/"], (_, _) => Task.FromResult<byte[]>([]));

        var refused = Assert.Throws<HttpListenerException>(loop.Start);

        Assert.Equal(OperatingSystem.IsWindows() ? 87 : 400, refused.ErrorCode);
        Assert.NotEqual("The port is in use.", refused.Message);
    }
}
