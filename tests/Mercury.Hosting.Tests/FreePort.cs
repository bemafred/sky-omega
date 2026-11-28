// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;

namespace SkyOmega.Mercury.Hosting.Tests;

/// <summary>
/// A port of the machine's for a test's mercuryd, mercury or host. The machine's ports are shared by every listener on
/// it: the other tests, which run in parallel, the other test processes, and on Windows every connection's own end. A
/// port is known to be free only while it is held, and a mercuryd binds its port in a process of its own, well after
/// the system named it. So the system names a free port, the endpoint starts on it, and if it says "The port is in
/// use.", it is started again on another port (as Mercury.Tests' FreePort starts an endpoint of the test's own).
/// </summary>
internal static class FreePort
{
    /// <summary>
    /// How many times an endpoint is started on a free port before the port in use is the test's: a race lost once is
    /// chance, but three in a row are not.
    /// </summary>
    internal const int Starts = 3;

    /// <summary>A port that the system names as free, which it is until another takes it.</summary>
    public static int Name()
    {
        var named = new TcpListener(IPAddress.Loopback, 0);
        named.Start();
        int port = ((IPEndPoint)named.LocalEndpoint).Port;
        named.Stop();
        return port;
    }

    /// <summary>
    /// The endpoint that <paramref name="start"/> started on a free port, and that port. <paramref name="start"/> gives
    /// null when the endpoint said that the port is in use, once it has disposed of it, and it is started again on
    /// another port, up to <see cref="Starts"/> times.
    /// </summary>
    public static async Task<(T Endpoint, int Port)> Start<T>(Func<int, Task<T?>> start) where T : class
    {
        for (var starts = 1; starts <= Starts; starts++)
        {
            var port = Name();
            if (await start(port) is { } endpoint)
                return (endpoint, port);
        }
        throw new InvalidOperationException($"The port was in use at each of {Starts} starts.");
    }
}
