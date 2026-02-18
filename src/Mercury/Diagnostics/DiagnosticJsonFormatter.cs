// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Formats diagnostics as JSON for LSP and tooling integration.
/// </summary>
/// <remarks>
/// Output follows LSP Diagnostic structure:
/// <code>
/// {
///   "diagnostics": [
///     {
///       "range": {
///         "start": { "line": 1, "character": 4 },
///         "end": { "line": 1, "character": 8 }
///       },
///       "severity": 1,
///       "code": "E2001",
///       "source": "mercury-sparql",
///       "message": "undefined prefix 'foaf'"
///     }
///   ]
/// }
/// </code>
/// </remarks>
internal sealed class DiagnosticJsonFormatter
{
    private readonly string _source;

    /// <summary>
    /// Creates a JSON formatter.
    /// </summary>
    /// <param name="source">The source identifier (e.g., "mercury-sparql").</param>
    public DiagnosticJsonFormatter(string source = "mercury-sparql")
    {
        _source = source;
    }

    /// <summary>
    /// Formats all diagnostics as a JSON string.
    /// </summary>
    public string Format(ref DiagnosticBag bag)
    {
        using var stream = new MemoryStream();
        FormatToStream(ref bag, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Formats all diagnostics to a stream.
    /// </summary>
    public void FormatToStream(ref DiagnosticBag bag, Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true
        });

        writer.WriteStartObject();
        writer.WriteStartArray("diagnostics");

        foreach (var diagnostic in bag)
        {
            WriteDiagnostic(writer, in diagnostic, ref bag);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>
    /// Formats a single diagnostic as a JSON string.
    /// </summary>
    public string FormatSingle(in Diagnostic diagnostic, ref DiagnosticBag bag)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        WriteDiagnostic(writer, in diagnostic, ref bag);

        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteDiagnostic(Utf8JsonWriter writer, in Diagnostic diagnostic, ref DiagnosticBag bag)
    {
        writer.WriteStartObject();

        // Range (LSP uses 0-based lines and characters)
        writer.WriteStartObject("range");
        WritePosition(writer, "start", diagnostic.Span.Line - 1, diagnostic.Span.Column - 1);
        WritePosition(writer, "end", diagnostic.Span.Line - 1, diagnostic.Span.Column - 1 + diagnostic.Span.Length);
        writer.WriteEndObject();

        // Severity (LSP: 1=Error, 2=Warning, 3=Information, 4=Hint)
        var lspSeverity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => 1,
            DiagnosticSeverity.Warning => 2,
            DiagnosticSeverity.Info => 3,
            DiagnosticSeverity.Hint => 4,
            _ => 1
        };
        writer.WriteNumber("severity", lspSeverity);

        // Code
        writer.WriteString("code", diagnostic.CodeString);

        // Source
        writer.WriteString("source", _source);

        // Message
        var message = DiagnosticMessages.Format(in diagnostic, ref bag);
        writer.WriteString("message", message);

        // Related information (if present)
        if (diagnostic.HasRelatedSpan)
        {
            writer.WriteStartArray("relatedInformation");
            writer.WriteStartObject();

            writer.WriteStartObject("location");
            writer.WriteString("uri", ""); // Would need URI from context
            writer.WriteStartObject("range");
            WritePosition(writer, "start", diagnostic.RelatedSpan.Line - 1, diagnostic.RelatedSpan.Column - 1);
            WritePosition(writer, "end", diagnostic.RelatedSpan.Line - 1, diagnostic.RelatedSpan.Column - 1 + diagnostic.RelatedSpan.Length);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteString("message", "previously defined here");
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        // Tags for deprecated/unnecessary
        if (diagnostic.Code == DiagnosticCode.DeprecatedSyntax)
        {
            writer.WriteStartArray("tags");
            writer.WriteNumberValue(2); // LSP DiagnosticTag.Deprecated
            writer.WriteEndArray();
        }
        else if (diagnostic.Code == DiagnosticCode.RedundantDistinct)
        {
            writer.WriteStartArray("tags");
            writer.WriteNumberValue(1); // LSP DiagnosticTag.Unnecessary
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WritePosition(Utf8JsonWriter writer, string name, int line, int character)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("line", Math.Max(0, line));
        writer.WriteNumber("character", Math.Max(0, character));
        writer.WriteEndObject();
    }

    /// <summary>
    /// Gets the LSP severity value for a diagnostic severity.
    /// </summary>
    public static int ToLspSeverity(DiagnosticSeverity severity)
    {
        return severity switch
        {
            DiagnosticSeverity.Error => 1,
            DiagnosticSeverity.Warning => 2,
            DiagnosticSeverity.Info => 3,
            DiagnosticSeverity.Hint => 4,
            _ => 1
        };
    }
}
