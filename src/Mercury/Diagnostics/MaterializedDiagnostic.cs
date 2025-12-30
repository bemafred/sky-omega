// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// A materialized diagnostic that can be stored in collections.
/// </summary>
/// <remarks>
/// Unlike <see cref="Diagnostic"/> which references pooled buffers,
/// this class owns its string data and can be stored freely.
/// </remarks>
public sealed class MaterializedDiagnostic
{
    /// <summary>
    /// The diagnostic code.
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// The severity level.
    /// </summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>
    /// The formatted code string (e.g., "E2001").
    /// </summary>
    public string CodeString { get; }

    /// <summary>
    /// The formatted message with arguments substituted.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Source location.
    /// </summary>
    public SourceSpan Span { get; }

    /// <summary>
    /// Related source location (if any).
    /// </summary>
    public SourceSpan? RelatedSpan { get; }

    /// <summary>
    /// Creates a materialized diagnostic.
    /// </summary>
    public MaterializedDiagnostic(
        int code,
        DiagnosticSeverity severity,
        string message,
        SourceSpan span,
        SourceSpan? relatedSpan = null)
    {
        Code = code;
        Severity = severity;
        CodeString = DiagnosticCode.FormatCode(code);
        Message = message;
        Span = span;
        RelatedSpan = relatedSpan;
    }

    /// <summary>
    /// Creates a materialized diagnostic from a Diagnostic and DiagnosticBag.
    /// </summary>
    public static MaterializedDiagnostic FromDiagnostic(in Diagnostic diagnostic, ref DiagnosticBag bag)
    {
        var message = DiagnosticMessages.Format(in diagnostic, ref bag);
        return new MaterializedDiagnostic(
            diagnostic.Code,
            diagnostic.Severity,
            message,
            diagnostic.Span,
            diagnostic.HasRelatedSpan ? diagnostic.RelatedSpan : null);
    }

    /// <summary>
    /// Materializes all diagnostics from a bag.
    /// </summary>
    public static List<MaterializedDiagnostic> FromBag(ref DiagnosticBag bag)
    {
        var list = new List<MaterializedDiagnostic>(bag.Count);
        foreach (var diag in bag)
        {
            list.Add(FromDiagnostic(in diag, ref bag));
        }
        return list;
    }

    /// <summary>
    /// Returns true if this is an error.
    /// </summary>
    public bool IsError => DiagnosticCode.IsError(Code);

    public override string ToString() => $"{CodeString}: {Message}";
}
