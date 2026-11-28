// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using SkyOmega.Mercury.Tests.Fixtures;

namespace SkyOmega.Mercury.Tests.Sparql;

/// <summary>
/// A SPARQL endpoint of a test's own, which answers each query with one row, <c>?gate</c> bound to "opened", only once
/// the test opens it. It says when it is first asked, so a test can act while a query waits on it.
/// </summary>
/// <remarks>
/// Disposing it opens it and waits for every answer it began, so that none is still being written when the test ends.
/// An answer to a query whose HTTP client has given up (100 s) fails at once, so the wait is bounded by that.
/// </remarks>
internal sealed class GatedSparqlEndpoint : IDisposable
{
    private readonly HttpListener _listener;
    private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentBag<Task> _answering = [];

    public GatedSparqlEndpoint()
    {
        _listener = FreePort.Start(port =>
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            return listener;
        }, listener => listener.Start(), out var port);
        Url = $"http://localhost:{port}/sparql";
        _ = AnswerAsync();
    }

    /// <summary>The endpoint's address, for a SERVICE clause.</summary>
    public string Url { get; }

    /// <summary>Completes when the endpoint is first asked.</summary>
    public Task Asked => _asked.Task;

    /// <summary>Lets the endpoint answer the queries waiting on it and every one after.</summary>
    public void Open() => _opened.TrySetResult();

    public void Dispose()
    {
        Open();
        try
        {
            Task.WhenAll(_answering).Wait(TimeSpan.FromSeconds(150));
        }
        finally
        {
            _listener.Close();
        }
    }

    private async Task AnswerAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (!_listener.IsListening)
            {
                return;
            }
            _answering.Add(Task.Run(() => AnswerWhenOpenedAsync(context)));
        }
    }

    private async Task AnswerWhenOpenedAsync(HttpListenerContext context)
    {
        _asked.TrySetResult();
        await _opened.Task;
        var body = Encoding.UTF8.GetBytes(
            """{"head":{"vars":["gate"]},"results":{"bindings":[{"gate":{"type":"literal","value":"opened"}}]}}""");
        context.Response.ContentType = "application/sparql-results+json";
        try
        {
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
        catch (Exception)
        {
            // The query that asked has gone (its HTTP client gave up), or the endpoint is closing.
        }
    }
}
