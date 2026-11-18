#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:package Microsoft.Extensions.Hosting@10.0.0
//
// DrHook.Engine probe 112 target — a program asked to end through its own shutdown mechanism (ADR-016 Step 3).
//
// Prints its PID, waits up to 30 s for a debugger to attach (Debugger.IsAttached), then floods and waits to be asked to
// end. It appends what happened to the marker file, one word per line, so the probe can tell which part of its
// shutdown ran. It never breaks into a debugger. Whatever happens, it returns within 60 s of starting, unless it is
// held stopped by a debugger: at the cap it writes "cap" and returns.
//
// The flood is probe 100's: a thread starts and joins one empty thread after another, so an attached debugger is
// always dispatching CreateThread and ExitThread events, and reading this program's memory to do it.
//
// Arguments: <kind> [marker file]. Kinds:
//   idle             no flood; returns 3 s after the attach. The probe's final attach check.
//   term-handler     a SIGTERM handler (PosixSignalRegistration) cancels the signal's default and the flood; the
//                    program joins the flood and returns 0. Marker: "signal", then "cancelled".
//   term-default     no handler of its own: the runtime's default handling of SIGTERM. A ProcessExit handler writes
//                    "process-exit" if it runs. What the process's end looks like is recorded, not predicted.
//   host-term        a Generic Host (Microsoft.Extensions.Hosting) whose BackgroundService floods; SIGTERM reaches the
//                    host's console lifetime, which stops the host. Marker: "stopping", "service-stopped",
//                    "host-stopped".
//   host-self-stop   the same host, stopped by itself through IHostApplicationLifetime.StopApplication 1.5 s after its
//                    flood began; no signal. Marker: "stopping", "service-stopped", "host-stopped".
// Windows has no SIGTERM to send; its request to a console program is CTRL+BREAK, which .NET reports as SIGQUIT. The
// Windows kinds are the same programs asked that way:
//   break-handler    as term-handler, with its handler on SIGQUIT (cancelling it is supported on Windows).
//   break-default    as term-default: no handler, so Windows' own handling of CTRL+BREAK applies.
//   host-break       as host-term: CTRL+BREAK reaches the host's console lifetime as SIGQUIT.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var started = Stopwatch.StartNew();
TimeSpan cap = TimeSpan.FromSeconds(60);
string kind = args.Length > 0 ? args[0] : "idle";
string? marker = args.Length > 1 ? args[1] : null;
Console.WriteLine($"PROBE112_TARGET pid={Environment.ProcessId}");
Console.Out.Flush();

while (!Debugger.IsAttached && started.Elapsed < TimeSpan.FromSeconds(30))
    Thread.Sleep(5);

switch (kind)
{
    case "idle":
        Thread.Sleep(Remaining(TimeSpan.FromSeconds(3)));
        Mark("done");
        return 0;

    case "term-handler":
    case "break-handler":
    {
        using var cancelled = new ManualResetEventSlim(false);
        PosixSignal signal = kind == "break-handler" ? PosixSignal.SIGQUIT : PosixSignal.SIGTERM;
        using PosixSignalRegistration registration = PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            Mark("signal");
            cancelled.Set();
        });
        Thread flood = StartFlood(() => cancelled.IsSet);
        cancelled.Wait(Remaining(cap));
        flood.Join();
        Mark(cancelled.IsSet ? "cancelled" : "cap");
        return 0;
    }

    case "term-default":
    case "break-default":
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Mark("process-exit");
        StartFlood(static () => false);
        Thread.Sleep(Remaining(cap));
        Mark("cap");
        return 0;
    }

    case "host-term":
    case "host-break":
    case "host-self-stop":
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new Shared(Mark, started, cap, kind == "host-self-stop"));
        builder.Services.AddHostedService<FloodService>();
        using IHost host = builder.Build();
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() => Mark("stopping"));
        host.Run();
        Mark("host-stopped");
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown kind: {kind}");
        return 2;
}

void Mark(string word)
{
    if (marker is null) return;
    lock (started) File.AppendAllText(marker, word + "\n");
}

TimeSpan Remaining(TimeSpan span)
{
    TimeSpan left = cap - started.Elapsed;
    if (left > span) left = span;
    return left > TimeSpan.Zero ? left : TimeSpan.Zero;
}

Thread StartFlood(Func<bool> stop)
{
    var flooder = new Thread(() =>
    {
        while (!stop() && started.Elapsed < cap)
        {
            Thread t = new(static () => { });
            t.Start();
            t.Join();
        }
    }) { IsBackground = true, Name = "flood" };
    flooder.Start();
    return flooder;
}

sealed record Shared(Action<string> Mark, Stopwatch Started, TimeSpan Cap, bool SelfStop);

// Floods until the host stops it. host-self-stop asks the host to stop 1.5 s after the flood began; both kinds ask it
// at the cap, so the program always ends.
sealed class FloodService(Shared shared, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flooder = new Thread(() =>
        {
            var flooding = Stopwatch.StartNew();
            while (!stoppingToken.IsCancellationRequested)
            {
                if (shared.Started.Elapsed >= shared.Cap) { shared.Mark("cap"); lifetime.StopApplication(); break; }
                if (shared.SelfStop && flooding.Elapsed >= TimeSpan.FromSeconds(1.5)) { lifetime.StopApplication(); break; }
                Thread t = new(static () => { });
                t.Start();
                t.Join();
            }
        }) { IsBackground = true, Name = "flood" };
        flooder.Start();
        var done = new TaskCompletionSource();
        stoppingToken.Register(() =>
        {
            if (Thread.CurrentThread != flooder) flooder.Join(); // the flood thread itself may be the one that stops the host
            shared.Mark("service-stopped");
            done.TrySetResult();
        });
        return done.Task;
    }
}
