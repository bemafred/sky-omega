// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;

namespace SkyOmega.Mercury.Tests.Fixtures;

/// <summary>
/// A port of the machine's for a test's endpoint. The machine's ports are shared by every listener on it: the other
/// tests, which run in parallel, the other test processes, and on Windows every connection's own end, which takes its
/// port from the range the system names free ones from. A port is known to be free only while it is held. So the
/// system names a free port, the endpoint starts on it, and if the start says that another took the port first, the
/// endpoint is made again on another port.
/// </summary>
/// <remarks>
/// An HttpListener cannot be given port 0 to take one the system chooses, and a refused start disposes it. A start
/// says its port is taken with these codes (measured on Linux, macOS and Windows on 2026-11-24,
/// tools/probes/free-port-race):
/// <list type="bullet">
/// <item>Windows: ERROR_SHARING_VIOLATION (32) when a socket holds the port, and ERROR_ALREADY_EXISTS (183) when
/// another HttpListener has the prefix.</item>
/// <item>Linux and macOS: EADDRINUSE (98 on Linux, 48 on macOS), and 400 when another HttpListener in the process has
/// the prefix.</item>
/// </list>
/// </remarks>
internal static class FreePort
{
    /// <summary>
    /// How many times an endpoint is started on a free port before the refusal is the test's: a race lost
    /// once is chance, but three in a row are not.
    /// </summary>
    internal const int Starts = 3;

    /// <summary>
    /// The endpoint that <paramref name="create"/> makes on a free port, started by <paramref name="start"/>, and that
    /// port. An endpoint whose start says its port is taken is disposed, and another is made on another port, up to
    /// <see cref="Starts"/> times. Any other refusal, and the last, is thrown, and its endpoint is left as its start
    /// left it, for the test.
    /// </summary>
    public static T Start<T>(Func<int, T> create, Action<T> start, out int port) where T : IDisposable
    {
        for (var starts = 1; ; starts++)
        {
            var named = new TcpListener(IPAddress.Loopback, 0);
            named.Start();
            port = ((IPEndPoint)named.LocalEndpoint).Port;
            named.Stop();

            var endpoint = create(port);
            try
            {
                start(endpoint);
                return endpoint;
            }
            catch (HttpListenerException refused) when (starts < Starts && IsTaken(refused))
            {
                endpoint.Dispose();
            }
        }
    }

    private static bool IsTaken(HttpListenerException refused) => OperatingSystem.IsWindows()
        ? refused.ErrorCode is 32 or 183
        : refused.ErrorCode == 400 || refused.ErrorCode == (OperatingSystem.IsMacOS() ? 48 : 98);
}
