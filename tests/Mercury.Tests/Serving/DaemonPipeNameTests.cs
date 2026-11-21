// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipes;
using System.Text;
using SkyOmega.Mercury.Runtime.IO;
using Xunit;

namespace SkyOmega.Mercury.Tests.Serving;

/// <summary>
/// Where the attach socket of a store's mercuryd is (ADR-065 D6), for the daemon and its clients: in the store's
/// directory, up to the longest path a socket's address holds, and by the store's name beyond it and on Windows.
/// </summary>
public sealed class DaemonPipeNameTests
{
    [Fact]
    public async Task OnLinuxAndMacOS_ItIsTheSocketInTheStore_UpToTheLongestPathASocketHolds_AndOneByteMoreIsNoSocket()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A Windows pipe has no path: its name is the store's name.");
        var ct = TestContext.Current.CancellationToken;
        // Measured on zbook (Linux), 2026-11-21: a socket path of 107 bytes is bound, and one of 108 refused. macOS
        // holds 104 bytes with the terminating zero. The binds below hold both numbers to the platform the test runs on.
        int longest = OperatingSystem.IsLinux() ? 107 : 103;
        var root = Directory.CreateTempSubdirectory("ms").FullName;
        try
        {
            // <root>/<store>/mercuryd.sock, with the store's name as long as makes the whole path that many bytes.
            string StoreOfSocketLength(int bytes) => Path.Combine(root,
                new string('s', bytes - Encoding.UTF8.GetByteCount(root) - "/".Length - "/mercuryd.sock".Length));
            var atLongest = StoreOfSocketLength(longest);
            var beyond = StoreOfSocketLength(longest + 1);
            Directory.CreateDirectory(atLongest);
            Directory.CreateDirectory(beyond);

            var socket = MercuryPorts.DaemonPipeName(atLongest);
            Assert.Equal(Path.Combine(atLongest, "mercuryd.sock"), socket);
            Assert.Equal(longest, Encoding.UTF8.GetByteCount(socket));
            await using (var server = new NamedPipeServerStream(socket, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            await using (var client = new NamedPipeClientStream(".", socket, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                var connecting = server.WaitForConnectionAsync(ct);
                await client.ConnectAsync(5000, ct);
                await connecting;
            }

            Assert.Equal($"mercury-{Path.GetFileName(beyond)}", MercuryPorts.DaemonPipeName(beyond));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new NamedPipeServerStream(Path.Combine(beyond, "mercuryd.sock"), PipeDirection.InOut, 1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OnWindows_ItIsThePipeOfTheStoresName()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "On Linux and macOS it is a socket in the store's directory.");

        Assert.Equal("mercury-shared", MercuryPorts.DaemonPipeName(@"C:\stores\shared"));
    }

    [Fact]
    public void AStorePathWithATrailingSeparator_IsTheSameStore()
    {
        var store = Path.Combine(Path.GetTempPath(), "shared");

        Assert.Equal(MercuryPorts.DaemonPipeName(store), MercuryPorts.DaemonPipeName(store + Path.DirectorySeparatorChar));
    }
}
