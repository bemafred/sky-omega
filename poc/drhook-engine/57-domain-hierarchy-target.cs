#!/usr/bin/env -S dotnet
//
// DrHook.Engine probe 57 TARGET — defines a TWO-LEVEL exception hierarchy entirely in the target's
// own module (MyApp.DomainException : Exception, MyApp.OrderValidationException : DomainException),
// then throws the DERIVED type in a loop. Lets the probe arm an exception filter on the BASE
// (MyApp.DomainException) and verify the substrate's subclass-chain walk matches across the
// target's own module — not just BCL types. Probes 26-30 covered exception filtering against
// target-defined types, but always against the runtime type directly; this is the first probe
// where the FILTER is on a target-defined BASE and the actual stop is on a target-defined DERIVED.

using System;
using System.Diagnostics;
using System.Threading;

Console.WriteLine($"READY {Environment.ProcessId}");
Console.Out.Flush();

for (int i = 0; i < 500 && !Debugger.IsAttached; i++) Thread.Sleep(10);
Debugger.Break();

while (true)
{
    try
    {
        throw new MyApp.OrderValidationException("probe 57 derived exception", orderId: 42);
    }
    catch (MyApp.DomainException)
    {
        // swallow — the first-chance Exception callback already fired at the throw site,
        // and the substrate's MatchesChain should have admitted it via DomainException base match.
    }
    Thread.Sleep(20);
}

namespace MyApp
{
    public class DomainException : Exception
    {
        public DomainException(string message) : base(message) { }
    }

    public sealed class OrderValidationException : DomainException
    {
        public int OrderId { get; }
        public OrderValidationException(string message, int orderId) : base(message) => OrderId = orderId;
    }
}
