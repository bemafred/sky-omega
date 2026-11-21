// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using SkyOmega.Mercury.Runtime.IO;

namespace SkyOmega.Mercury.Serving;

/// <summary><c>mercury --attach</c>: the REPL of a Mercury process that hosts a pipe, on this console.</summary>
public static class AttachClient
{
    /// <summary>How long a connection is waited for.</summary>
    public const int ConnectTimeoutMs = 5000;

    /// <summary>Connects to <paramref name="target"/>'s pipe and runs its REPL on the console until the user leaves
    /// it.</summary>
    /// <exception cref="TimeoutException">Nothing answered on the pipe in time.</exception>
    public static async Task RunAsync(AttachTarget target, TextWriter output)
    {
        output.WriteLine($"Attaching to {target.Description} via pipe '{target.PipeName}'...");
        using var client = new PipeClient(target.PipeName);
        await client.ConnectAsync(timeoutMs: ConnectTimeoutMs);
        await client.RunInteractiveAsync();
    }

    /// <summary>
    /// <see cref="RunAsync"/> for a tool that attaches and exits: says on <paramref name="output"/> why it could not,
    /// and returns the exit code, 0 when it attached.
    /// </summary>
    public static async Task<int> RunToExitCodeAsync(AttachTarget target, TextWriter output)
    {
        try
        {
            await RunAsync(target, output);
            return 0;
        }
        catch (TimeoutException)
        {
            output.WriteLine($"Error: Could not connect to {target.Description}. Is it running?");
            return 1;
        }
        catch (Exception ex)
        {
            output.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }
}
