using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Federated;

/// <summary>
/// Interface for executing SPARQL queries against remote endpoints.
/// Implementations handle HTTP communication and result parsing.
/// </summary>
internal interface ISparqlServiceExecutor
{
    /// <summary>
    /// Executes a SELECT query against a remote SPARQL endpoint.
    /// </summary>
    /// <param name="endpointUri">The SPARQL endpoint URI.</param>
    /// <param name="query">The SPARQL SELECT query to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of result rows with variable bindings.</returns>
    ValueTask<List<ServiceResultRow>> ExecuteSelectAsync(
        string endpointUri,
        string query,
        CancellationToken ct = default);

    /// <summary>
    /// Executes an ASK query against a remote SPARQL endpoint.
    /// </summary>
    /// <param name="endpointUri">The SPARQL endpoint URI.</param>
    /// <param name="query">The SPARQL ASK query to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Boolean result of the ASK query.</returns>
    ValueTask<bool> ExecuteAskAsync(
        string endpointUri,
        string query,
        CancellationToken ct = default);
}

/// <summary>
/// Represents a single result row from a remote SPARQL query.
/// Maps variable names to their bound values.
/// </summary>
internal sealed class ServiceResultRow
{
    private readonly Dictionary<string, ServiceBinding> _bindings;

    public ServiceResultRow()
    {
        _bindings = new Dictionary<string, ServiceBinding>(StringComparer.Ordinal);
    }

    public int Count => _bindings.Count;

    public IEnumerable<string> Variables => _bindings.Keys;

    public void AddBinding(string variable, ServiceBinding binding)
    {
        _bindings[variable] = binding;
    }

    public bool TryGetBinding(string variable, out ServiceBinding binding)
    {
        return _bindings.TryGetValue(variable, out binding);
    }

    public ServiceBinding GetBinding(string variable)
    {
        return _bindings.TryGetValue(variable, out var binding) ? binding : default;
    }
}

/// <summary>
/// Represents a bound value in a SPARQL result.
/// </summary>
internal readonly struct ServiceBinding
{
    public ServiceBinding(ServiceBindingType type, string value, string? datatype = null, string? language = null)
    {
        Type = type;
        Value = value;
        Datatype = datatype;
        Language = language;
    }

    public ServiceBindingType Type { get; }
    public string Value { get; }
    public string? Datatype { get; }
    public string? Language { get; }

    /// <summary>
    /// Returns the value formatted as an RDF term for SPARQL processing.
    /// </summary>
    public string ToRdfTerm()
    {
        return Type switch
        {
            ServiceBindingType.Uri => $"<{Value}>",
            ServiceBindingType.Literal when Language != null => $"\"{Value}\"@{Language}",
            ServiceBindingType.Literal when Datatype != null => $"\"{Value}\"^^<{Datatype}>",
            ServiceBindingType.Literal => $"\"{Value}\"",
            ServiceBindingType.BNode => $"_:{Value}",
            _ => Value
        };
    }
}

/// <summary>
/// Type of a SPARQL result binding value.
/// </summary>
internal enum ServiceBindingType
{
    Uri,
    Literal,
    BNode
}

/// <summary>
/// Exception thrown when a SERVICE clause execution fails.
/// </summary>
internal class SparqlServiceException : Exception
{
    public SparqlServiceException(string message) : base(message) { }
    public SparqlServiceException(string message, Exception inner) : base(message, inner) { }

    public string? EndpointUri { get; init; }
    public string? Query { get; init; }
}
