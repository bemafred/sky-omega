// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using SkyOmega.Mercury.Runtime;
using Xunit;

namespace SkyOmega.Mercury.Hosting.Tests;

/// <summary>
/// A test's mercuryd, mercury or host on a free port (<see cref="FreePort"/>): it is started again on another port when
/// it says that the port is in use, which another took after the system named it, and only then. A test with a
/// mercuryd or a mercury takes the port as the arguments are made for it, between the naming and the start, which is
/// where another test or process takes it in a run. The host's is <c>StoreHostTests</c>'.
/// </summary>
public sealed class FreePortTests : IDisposable
{
    private readonly TempPath _dir;
    private readonly IReadOnlyDictionary<string, string> _environment = new Dictionary<string, string>();

    public FreePortTests()
    {
        _dir = TempPath.Test("free-port");
        _dir.MarkOwnership();
    }

    public void Dispose() => _dir.Cleanup();

    [Fact]
    public async Task AStart_ThatSaysThePortIsInUse_IsMadeAgain_OnANewlyNamedPort()
    {
        var ports = new List<int>();

        var (endpoint, port) = await FreePort.Start(named =>
        {
            ports.Add(named);
            return Task.FromResult<string?>(ports.Count == 1 ? null : "started");
        });

        Assert.Equal("started", endpoint);
        Assert.Equal(2, ports.Count);
        Assert.Equal(ports[1], port);
    }

    [Fact]
    public async Task AStart_ThatSaysThePortIsInUseEachTime_IsRefused_AtTheLastStart()
    {
        var ports = new List<int>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => FreePort.Start(named =>
        {
            ports.Add(named);
            return Task.FromResult<string?>(null);
        }));

        Assert.Equal(FreePort.Starts, ports.Count);
    }

    [Fact]
    public async Task AMercurydWhosePortIsTakenBeforeItStarts_IsServedOnAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        // On Windows the attach pipe is named by the store's name, one namespace for the machine.
        var store = Path.Combine(_dir.FullPath, $"s{Guid.NewGuid():N}"[..9]);
        var ports = new List<int>();
        TcpListener? taker = null;
        try
        {
            var (mercuryd, port) = await Mercuryd.Serve(named =>
            {
                ports.Add(named);
                if (ports.Count == 1)
                {
                    taker = new TcpListener(Dns.GetHostAddresses("localhost")[0], named);
                    taker.Start();
                }
                return ["-d", store, "-p", named.ToString()];
            }, ct);
            using (mercuryd)
            {
                Assert.Equal(2, ports.Count);
                Assert.Equal(ports[1], port);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var answer = await http.GetAsync($"http://localhost:{port}/sparql", ct);
                Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            }
        }
        finally
        {
            taker?.Stop();
        }
    }

    [Fact]
    public async Task AMercuryWhosePortIsTakenBeforeItStarts_IsServedOnAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var ports = new List<int>();
        TcpListener? taker = null;
        try
        {
            using var mercury = await MercuryCli.ServeOverHttp(_environment, named =>
            {
                ports.Add(named);
                if (ports.Count == 1)
                {
                    taker = new TcpListener(Dns.GetHostAddresses("localhost")[0], named);
                    taker.Start();
                }
                return ["-d", Path.Combine(_dir.FullPath, $"store-{named}"), "-p", named.ToString()];
            }, ct);

            Assert.Equal(2, ports.Count);
            Assert.Contains($"HTTP endpoint: http://localhost:{ports[1]}/sparql", mercury.Output);
            mercury.EndInput();
            Assert.Equal(0, await mercury.Ended(ct));
        }
        finally
        {
            taker?.Stop();
        }
    }
}
