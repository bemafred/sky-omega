// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using SkyOmega.Mercury.Tests.Fixtures;
using Xunit;

namespace SkyOmega.Mercury.Tests.Infrastructure;

/// <summary>
/// A test's endpoint on a free port (<see cref="FreePort"/>): it is made again on another port when its start says
/// that another took the port after the system named it, and only then. Each test takes the port inside the
/// endpoint's making, between the naming and the start, which is where another test or process takes it in a run.
/// </summary>
public sealed class FreePortTests
{
    [Fact]
    public void AStart_RefusedBecauseAnotherHttpListenerTookThePort_IsMadeAgainOnAnother_AndTheFirstDisposed()
    {
        var made = new List<(int Port, Endpoint Endpoint)>();
        var taker = new HttpListener();
        try
        {
            using var endpoint = FreePort.Start(port =>
            {
                if (made.Count == 0)
                {
                    taker.Prefixes.Add($"http://localhost:{port}/");
                    taker.Start();
                }
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                made.Add((port, new Endpoint(listener)));
                return made[^1].Endpoint;
            }, endpoint => endpoint.Listener.Start(), out var started);

            Assert.True(endpoint.Listener.IsListening);
            Assert.Equal(2, made.Count);
            Assert.NotEqual(made[0].Port, made[1].Port);
            Assert.Equal(made[1].Port, started);
            Assert.True(made[0].Endpoint.Disposed);
            Assert.False(made[1].Endpoint.Disposed);
        }
        finally
        {
            taker.Close();
        }
    }

    [Fact]
    public void AStart_RefusedBecauseASocketTookThePort_IsMadeAgainOnAnother()
    {
        // The managed listener of Linux and macOS listens on the first address localhost resolves to (127.0.0.1 on
        // zbook, [::1] on macOS); on Windows a socket on any address takes the port from http.sys.
        var ports = new List<int>();
        TcpListener? taker = null;
        try
        {
            using var listener = FreePort.Start(port =>
            {
                ports.Add(port);
                if (ports.Count == 1)
                {
                    taker = new TcpListener(Dns.GetHostAddresses("localhost")[0], port);
                    taker.Start();
                }
                var made = new HttpListener();
                made.Prefixes.Add($"http://localhost:{port}/");
                return made;
            }, made => made.Start(), out var started);

            Assert.True(listener.IsListening);
            Assert.Equal(2, ports.Count);
            Assert.Equal(ports[1], started);
        }
        finally
        {
            taker?.Stop();
        }
    }

    [Fact]
    public void AStart_RefusedAsTakenEachTime_IsThrown_AtTheLastStart()
    {
        var takers = new List<HttpListener>();
        try
        {
            Assert.Throws<HttpListenerException>(() => FreePort.Start(port =>
            {
                var taker = new HttpListener();
                taker.Prefixes.Add($"http://localhost:{port}/");
                taker.Start();
                takers.Add(taker);
                var made = new HttpListener();
                made.Prefixes.Add($"http://localhost:{port}/");
                return made;
            }, made => made.Start(), out _));

            Assert.Equal(FreePort.Starts, takers.Count);
        }
        finally
        {
            foreach (var taker in takers)
                taker.Close();
        }
    }

    [Fact]
    public void AStart_RefusedForAnotherReason_IsThrown_AtTheFirstStart_AndItsEndpointLeftForTheTest()
    {
        // ERROR_ACCESS_DENIED, which a Windows listener on an address without a URL reservation gets: a refusal that
        // another port does not change.
        var made = new List<Endpoint>();

        var refused = Assert.Throws<HttpListenerException>(() => FreePort.Start(
            port =>
            {
                made.Add(new Endpoint(new HttpListener()));
                return made[^1];
            },
            endpoint => throw new HttpListenerException(5),
            out _));

        Assert.Equal(5, refused.ErrorCode);
        var only = Assert.Single(made);
        Assert.False(only.Disposed);
        only.Dispose();
    }

    /// <summary>A test's endpoint, which says whether it was disposed.</summary>
    private sealed class Endpoint(HttpListener listener) : IDisposable
    {
        public HttpListener Listener => listener;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            listener.Close();
        }
    }
}
