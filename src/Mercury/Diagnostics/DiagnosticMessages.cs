// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Text;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Message templates for diagnostic codes.
/// </summary>
public static class DiagnosticMessages
{
    /// <summary>
    /// Gets the message template for a diagnostic code.
    /// </summary>
    /// <remarks>
    /// Templates use {0}, {1}, etc. for argument placeholders.
    /// </remarks>
    public static string GetTemplate(int code)
    {
        return code switch
        {
            // E1xxx - Lexical/Parse errors
            DiagnosticCode.UnexpectedCharacter => "unexpected character '{0}'",
            DiagnosticCode.UnterminatedString => "unterminated string literal",
            DiagnosticCode.UnterminatedIri => "unterminated IRI (missing '>')",
            DiagnosticCode.InvalidEscape => "invalid escape sequence '{0}'",
            DiagnosticCode.UnexpectedEndOfInput => "unexpected end of input",
            DiagnosticCode.UnexpectedToken => "unexpected token '{0}'",
            DiagnosticCode.ExpectedToken => "expected {0}, found '{1}'",
            DiagnosticCode.ExpectedExpression => "expected expression",
            DiagnosticCode.ExpectedPattern => "expected triple pattern",
            DiagnosticCode.MalformedNumber => "malformed numeric literal '{0}'",
            DiagnosticCode.MalformedDateTime => "malformed datetime literal '{0}'",
            DiagnosticCode.MalformedBoolean => "malformed boolean literal",
            DiagnosticCode.InvalidPrefixedName => "invalid prefixed name '{0}'",
            DiagnosticCode.InvalidBlankNode => "invalid blank node identifier '{0}'",
            DiagnosticCode.UnclosedDelimiter => "unclosed '{0}'",
            DiagnosticCode.MismatchedDelimiter => "mismatched delimiter: opened with '{0}', closed with '{1}'",

            // E2xxx - Semantic errors
            DiagnosticCode.UndefinedPrefix => "undefined prefix '{0}'",
            DiagnosticCode.UnboundVariable => "variable ?{0} is not bound",
            DiagnosticCode.AggregateInNonAggregate => "aggregate function in non-aggregate query",
            DiagnosticCode.NonGroupedVariable => "variable ?{0} must appear in GROUP BY or be aggregated",
            DiagnosticCode.InvalidPropertyPath => "invalid property path",
            DiagnosticCode.DuplicateVariable => "variable ?{0} is already bound",
            DiagnosticCode.UnknownFunction => "unknown function '{0}'",
            DiagnosticCode.WrongArgumentCount => "function '{0}' expects {1} arguments, got {2}",
            DiagnosticCode.TypeMismatch => "type mismatch: cannot {0} {1} and {2}",
            DiagnosticCode.InvalidArgumentType => "invalid argument type for '{0}': expected {1}",
            DiagnosticCode.InvalidGraphTarget => "invalid GRAPH target",
            DiagnosticCode.InvalidServiceEndpoint => "invalid SERVICE endpoint '{0}'",
            DiagnosticCode.SubqueryVariableConflict => "subquery variable ?{0} conflicts with outer query",
            DiagnosticCode.RecursiveSubquery => "recursive subquery detected",

            // E3xxx - Runtime/Execution errors
            DiagnosticCode.QueryTimeout => "query execution exceeded time limit ({0}ms)",
            DiagnosticCode.MemoryLimitExceeded => "query exceeded memory limit",
            DiagnosticCode.ServiceUnreachable => "SERVICE endpoint unreachable: {0}",
            DiagnosticCode.ServiceError => "SERVICE endpoint returned error: {0}",
            DiagnosticCode.LoadFetchFailed => "LOAD failed to fetch '{0}': {1}",
            DiagnosticCode.LoadParseFailed => "LOAD failed to parse content from '{0}'",
            DiagnosticCode.DivisionByZero => "division by zero",
            DiagnosticCode.InvalidCast => "cannot cast '{0}' to {1}",
            DiagnosticCode.InvalidRegex => "invalid regular expression: {0}",
            DiagnosticCode.ResultSetTooLarge => "result set exceeds maximum size ({0} rows)",

            // E4xxx - Storage errors
            DiagnosticCode.StoreNotFound => "store not found: {0}",
            DiagnosticCode.StoreLocked => "store is locked by another process",
            DiagnosticCode.WalCorrupted => "write-ahead log is corrupted",
            DiagnosticCode.CheckpointFailed => "checkpoint failed: {0}",
            DiagnosticCode.AtomStoreCorrupted => "atom store is corrupted",
            DiagnosticCode.IndexCorrupted => "index is corrupted",

            // W1xxx - Warnings
            DiagnosticCode.CartesianProduct => "query produces Cartesian product (no shared variables between patterns)",
            DiagnosticCode.UnboundFilterVariable => "variable ?{0} in FILTER is never bound",
            DiagnosticCode.RedundantDistinct => "DISTINCT is redundant (results already unique)",
            DiagnosticCode.LargeOffsetWithoutLimit => "large OFFSET without LIMIT may be slow",
            DiagnosticCode.EmptyPatternGroup => "empty pattern group matches everything",
            DiagnosticCode.DeprecatedSyntax => "deprecated syntax: {0}",
            DiagnosticCode.ServiceSilent => "SERVICE SILENT may hide errors from '{0}'",
            DiagnosticCode.PerformanceWarning => "performance: {0}",

            // I1xxx - Info/Hints
            DiagnosticCode.SuggestIndex => "consider adding an index for {0}",
            DiagnosticCode.QueryPlanAvailable => "use EXPLAIN to see query plan",
            DiagnosticCode.SuggestRewrite => "consider rewriting: {0}",
            DiagnosticCode.PrefixAutoRegistered => "prefix '{0}' auto-registered as {1}",

            _ => "unknown diagnostic"
        };
    }

    /// <summary>
    /// Formats a diagnostic message with its arguments.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to format.</param>
    /// <param name="bag">The bag containing argument data.</param>
    /// <returns>The formatted message.</returns>
    public static string Format(in Diagnostic diagnostic, ref DiagnosticBag bag)
    {
        var template = GetTemplate(diagnostic.Code);

        if (diagnostic.ArgCount == 0)
            return template;

        // Use StringBuilder for multi-arg formatting
        var sb = new StringBuilder(template.Length + 64);
        var templateSpan = template.AsSpan();
        int lastPos = 0;

        for (int i = 0; i < templateSpan.Length - 2; i++)
        {
            if (templateSpan[i] == '{' && char.IsDigit(templateSpan[i + 1]) && templateSpan[i + 2] == '}')
            {
                // Append text before placeholder
                sb.Append(templateSpan.Slice(lastPos, i - lastPos));

                // Get argument index and append value
                int argIndex = templateSpan[i + 1] - '0';
                var argValue = bag.GetArg(in diagnostic, argIndex);
                sb.Append(argValue);

                lastPos = i + 3;
                i += 2;
            }
        }

        // Append remaining text
        if (lastPos < templateSpan.Length)
            sb.Append(templateSpan.Slice(lastPos));

        return sb.ToString();
    }

    /// <summary>
    /// Writes a formatted diagnostic message to a span, returning bytes written.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to format.</param>
    /// <param name="bag">The bag containing argument data.</param>
    /// <param name="destination">The destination span.</param>
    /// <returns>Number of characters written, or -1 if buffer too small.</returns>
    public static int TryFormat(in Diagnostic diagnostic, ref DiagnosticBag bag, Span<char> destination)
    {
        var template = GetTemplate(diagnostic.Code);

        if (diagnostic.ArgCount == 0)
        {
            if (template.Length > destination.Length)
                return -1;
            template.AsSpan().CopyTo(destination);
            return template.Length;
        }

        // Format with arguments
        int writePos = 0;
        var templateSpan = template.AsSpan();
        int lastPos = 0;

        for (int i = 0; i < templateSpan.Length - 2; i++)
        {
            if (templateSpan[i] == '{' && char.IsDigit(templateSpan[i + 1]) && templateSpan[i + 2] == '}')
            {
                // Copy text before placeholder
                var segment = templateSpan.Slice(lastPos, i - lastPos);
                if (writePos + segment.Length > destination.Length)
                    return -1;
                segment.CopyTo(destination.Slice(writePos));
                writePos += segment.Length;

                // Copy argument value
                int argIndex = templateSpan[i + 1] - '0';
                var argValue = bag.GetArg(in diagnostic, argIndex);
                if (writePos + argValue.Length > destination.Length)
                    return -1;
                argValue.CopyTo(destination.Slice(writePos));
                writePos += argValue.Length;

                lastPos = i + 3;
                i += 2;
            }
        }

        // Copy remaining text
        if (lastPos < templateSpan.Length)
        {
            var remaining = templateSpan.Slice(lastPos);
            if (writePos + remaining.Length > destination.Length)
                return -1;
            remaining.CopyTo(destination.Slice(writePos));
            writePos += remaining.Length;
        }

        return writePos;
    }
}
