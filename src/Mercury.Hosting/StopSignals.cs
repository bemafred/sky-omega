// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Hosting;

/// <summary>
/// SIGTERM and SIGINT, handled until this is disposed: a signal cancels <see cref="Token"/>, and the process is not
/// ended by it (ADR-058 Q7).
/// </summary>
/// <remarks>
/// <para>
/// The Generic Host's console lifetime handles both signals only while the host runs: <c>RunAsync</c> disposes the
/// host, which removes its handlers, so a signal while the store was being closed got the default action and ended
/// the process in the middle of the close. A tool registers these before it runs its host and disposes them once the
/// store is closed: a signal stops the host, and the process stays up until the store is closed.
/// </para>
/// <para>
/// Claude Code stops an MCP server with SIGINT, then SIGTERM 100 ms later; a service manager sends SIGTERM.
/// </para>
/// </remarks>
public sealed class StopSignals : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly PosixSignalRegistration _onSigterm;
    private readonly PosixSignalRegistration _onSigint;

    private StopSignals()
    {
        _onSigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnStopSignal);
        _onSigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnStopSignal);
    }

    /// <summary>Handles SIGTERM and SIGINT from now until <see cref="Dispose"/>.</summary>
    public static StopSignals Register() => new();

    /// <summary>Cancelled by the first stop signal.</summary>
    public CancellationToken Token => _stopping.Token;

    /// <summary>The signals get their default action again.</summary>
    public void Dispose()
    {
        _onSigint.Dispose();
        _onSigterm.Dispose();
        _stopping.Dispose();
    }

    private void OnStopSignal(PosixSignalContext context)
    {
        context.Cancel = true; // no default action: the process ends when the tool returns from Main
        try { _stopping.Cancel(); }
        catch (ObjectDisposedException) { } // a signal racing the end of Main
    }
}
