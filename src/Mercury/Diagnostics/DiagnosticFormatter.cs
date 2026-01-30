// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Formats diagnostics for terminal display with optional color support.
/// </summary>
/// <remarks>
/// Output style inspired by Rust compiler diagnostics:
/// <code>
/// error[E2001]: undefined prefix 'foaf'
///  --> query:2:5
///   |
/// 2 |     foaf:name ?name .
///   |     ^^^^ prefix not declared
///   |
/// help: declare the prefix
///   |
///   | PREFIX foaf: &lt;http://xmlns.com/foaf/0.1/&gt;
/// </code>
/// </remarks>
public sealed class DiagnosticFormatter
{
    private readonly TextWriter _writer;
    private readonly bool _useColor;
    private readonly string _sourceName;

    // ANSI color codes
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Red = "\x1b[31m";
    private const string BrightRed = "\x1b[91m";
    private const string Yellow = "\x1b[33m";
    private const string BrightYellow = "\x1b[93m";
    private const string Blue = "\x1b[34m";
    private const string BrightBlue = "\x1b[94m";
    private const string Cyan = "\x1b[36m";
    private const string BrightCyan = "\x1b[96m";
    private const string Green = "\x1b[32m";
    private const string Magenta = "\x1b[35m";
    private const string Gray = "\x1b[90m";

    /// <summary>
    /// Creates a formatter writing to the specified TextWriter.
    /// </summary>
    /// <param name="writer">The output writer.</param>
    /// <param name="useColor">Whether to use ANSI color codes.</param>
    /// <param name="sourceName">Name to display for the source (e.g., "query", "input.rq").</param>
    public DiagnosticFormatter(TextWriter writer, bool useColor = true, string sourceName = "query")
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _useColor = useColor;
        _sourceName = sourceName;
    }

    /// <summary>
    /// Creates a formatter writing to Console.Out with automatic color detection.
    /// </summary>
    public static DiagnosticFormatter Console { get; } = new(
        System.Console.Out,
        useColor: !System.Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null,
        sourceName: "query");

    /// <summary>
    /// Formats all diagnostics in a bag.
    /// </summary>
    /// <param name="bag">The diagnostic bag.</param>
    /// <param name="source">The source text.</param>
    /// <param name="hints">Optional hints keyed by diagnostic code.</param>
    public void Format(ref DiagnosticBag bag, ReadOnlySpan<char> source, IReadOnlyDictionary<int, string>? hints = null)
    {
        if (bag.IsEmpty)
            return;

        // Parse source into lines for context display
        var lines = SplitLines(source);

        try
        {
            foreach (var diagnostic in bag)
            {
                FormatDiagnostic(in diagnostic, ref bag, source, lines, hints);
                _writer.WriteLine();
            }

            // Summary line
            WriteSummary(ref bag);
        }
        finally
        {
            if (lines != null)
                ArrayPool<LineInfo>.Shared.Return(lines);
        }
    }

    /// <summary>
    /// Formats a single diagnostic.
    /// </summary>
    public void FormatDiagnostic(
        in Diagnostic diagnostic,
        ref DiagnosticBag bag,
        ReadOnlySpan<char> source,
        LineInfo[]? lines,
        IReadOnlyDictionary<int, string>? hints)
    {
        // Header: error[E2001]: undefined prefix 'foaf'
        WriteHeader(in diagnostic, ref bag);

        // Location: --> query:2:5
        WriteLocation(in diagnostic);

        // Source context with underline
        if (lines != null && diagnostic.Span.Line > 0 && diagnostic.Span.Line <= lines.Length)
        {
            WriteSourceContext(in diagnostic, ref bag, source, lines);
        }

        // Related location if present
        if (diagnostic.HasRelatedSpan && lines != null)
        {
            WriteRelatedLocation(in diagnostic, source, lines);
        }

        // Hint if available
        if (hints != null && hints.TryGetValue(diagnostic.Code, out var hint))
        {
            WriteHint(hint);
        }
    }

    private void WriteHeader(in Diagnostic diagnostic, ref DiagnosticBag bag)
    {
        var (color, label) = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => (BrightRed, "error"),
            DiagnosticSeverity.Warning => (BrightYellow, "warning"),
            DiagnosticSeverity.Info => (BrightCyan, "info"),
            DiagnosticSeverity.Hint => (BrightBlue, "hint"),
            _ => (Reset, "diagnostic")
        };

        // error[E2001]: message
        WriteColored(Bold + color, label);
        WriteColored(Bold, "[");
        WriteColored(Bold + color, diagnostic.CodeString);
        WriteColored(Bold, "]");
        WriteColored(Bold, ": ");

        var message = DiagnosticMessages.Format(in diagnostic, ref bag);
        WriteColored(Bold, message);
        _writer.WriteLine();
    }

    private void WriteLocation(in Diagnostic diagnostic)
    {
        // --> query:2:5
        WriteColored(BrightBlue, " --> ");
        _writer.Write(_sourceName);
        _writer.Write(':');
        _writer.Write(diagnostic.Span.Line);
        _writer.Write(':');
        _writer.WriteLine(diagnostic.Span.Column);
    }

    private void WriteSourceContext(
        in Diagnostic diagnostic,
        ref DiagnosticBag bag,
        ReadOnlySpan<char> source,
        LineInfo[] lines)
    {
        var lineIndex = diagnostic.Span.Line - 1;
        if (lineIndex < 0 || lineIndex >= lines.Length)
            return;

        var lineInfo = lines[lineIndex];
        var lineText = source.Slice(lineInfo.Start, lineInfo.Length);
        var lineNumWidth = diagnostic.Span.Line.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

        // Empty gutter line
        WriteGutter(lineNumWidth, null);
        _writer.WriteLine();

        // Source line: 2 |     foaf:name ?name .
        WriteGutter(lineNumWidth, diagnostic.Span.Line);
        WriteColored(Reset, lineText.ToString());
        _writer.WriteLine();

        // Underline:   |     ^^^^ message
        WriteGutter(lineNumWidth, null);
        WriteUnderline(in diagnostic, ref bag, lineInfo);
        _writer.WriteLine();
    }

    private void WriteGutter(int width, int? lineNum)
    {
        if (lineNum.HasValue)
        {
            var numStr = lineNum.Value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width);
            WriteColored(BrightBlue, numStr);
            WriteColored(BrightBlue, " | ");
        }
        else
        {
            WriteColored(BrightBlue, new string(' ', width));
            WriteColored(BrightBlue, " | ");
        }
    }

    private void WriteUnderline(in Diagnostic diagnostic, ref DiagnosticBag bag, LineInfo lineInfo)
    {
        var column = diagnostic.Span.Column;
        var length = diagnostic.Span.Length;

        // Clamp to line bounds
        if (column < 1) column = 1;
        var maxLen = lineInfo.Length - (column - 1);
        if (length > maxLen) length = maxLen;
        if (length < 1) length = 1;

        // Spaces to column position
        _writer.Write(new string(' ', column - 1));

        // Underline carets
        var underlineChar = diagnostic.Severity == DiagnosticSeverity.Error ? '^' : '~';
        var color = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => BrightRed,
            DiagnosticSeverity.Warning => BrightYellow,
            _ => BrightCyan
        };

        WriteColored(color, new string(underlineChar, length));

        // Short message after underline for single-line diagnostics
        var shortMsg = GetShortMessage(diagnostic.Code);
        if (!string.IsNullOrEmpty(shortMsg))
        {
            _writer.Write(' ');
            WriteColored(color, shortMsg);
        }
    }

    private void WriteRelatedLocation(in Diagnostic diagnostic, ReadOnlySpan<char> source, LineInfo[] lines)
    {
        var relSpan = diagnostic.RelatedSpan;
        if (relSpan.Line <= 0 || relSpan.Line > lines.Length)
            return;

        var lineNumWidth = Math.Max(diagnostic.Span.Line, relSpan.Line).ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

        // Empty line
        WriteGutter(lineNumWidth, null);
        _writer.WriteLine();

        // Note label
        WriteColored(BrightBlue, " = ");
        WriteColored(Bold, "note: ");
        _writer.WriteLine("previously defined here");

        // Location
        WriteColored(BrightBlue, " --> ");
        _writer.Write(_sourceName);
        _writer.Write(':');
        _writer.Write(relSpan.Line);
        _writer.Write(':');
        _writer.WriteLine(relSpan.Column);
    }

    private void WriteHint(string hint)
    {
        // help: declare the prefix
        WriteColored(BrightGreen, "help");
        WriteColored(Reset, ": ");
        _writer.WriteLine(hint);

        // If hint contains a code suggestion, format it
        if (hint.Contains('\n'))
        {
            WriteColored(BrightBlue, "  | ");
            _writer.WriteLine();

            foreach (var line in hint.Split('\n'))
            {
                WriteColored(BrightBlue, "  | ");
                WriteColored(Green, line);
                _writer.WriteLine();
            }
        }
    }

    private static readonly string BrightGreen = "\x1b[92m";

    private void WriteSummary(ref DiagnosticBag bag)
    {
        int errors = 0, warnings = 0;

        foreach (var diag in bag)
        {
            if (diag.IsError) errors++;
            else if (diag.Severity == DiagnosticSeverity.Warning) warnings++;
        }

        if (errors > 0 || warnings > 0)
        {
            var parts = new List<string>();

            if (errors > 0)
            {
                var errText = errors == 1 ? "1 error" : $"{errors} errors";
                parts.Add(_useColor ? $"{BrightRed}{errText}{Reset}" : errText);
            }

            if (warnings > 0)
            {
                var warnText = warnings == 1 ? "1 warning" : $"{warnings} warnings";
                parts.Add(_useColor ? $"{BrightYellow}{warnText}{Reset}" : warnText);
            }

            _writer.Write(string.Join(", ", parts));
            _writer.WriteLine(" emitted");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteColored(string color, string text)
    {
        if (_useColor)
        {
            _writer.Write(color);
            _writer.Write(text);
            _writer.Write(Reset);
        }
        else
        {
            _writer.Write(text);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteColored(string color, ReadOnlySpan<char> text)
    {
        if (_useColor)
        {
            _writer.Write(color);
            _writer.Write(text);
            _writer.Write(Reset);
        }
        else
        {
            _writer.Write(text);
        }
    }

    /// <summary>
    /// Gets a short message for the underline annotation.
    /// </summary>
    private static string GetShortMessage(int code)
    {
        return code switch
        {
            DiagnosticCode.UndefinedPrefix => "prefix not declared",
            DiagnosticCode.UnboundVariable => "not bound",
            DiagnosticCode.UnexpectedToken => "unexpected",
            DiagnosticCode.ExpectedToken => "expected here",
            DiagnosticCode.UnterminatedString => "missing closing quote",
            DiagnosticCode.UnterminatedIri => "missing '>'",
            DiagnosticCode.UnexpectedEndOfInput => "unexpected end",
            DiagnosticCode.UnknownFunction => "unknown",
            DiagnosticCode.TypeMismatch => "type error",
            DiagnosticCode.CartesianProduct => "no join condition",
            _ => ""
        };
    }

    /// <summary>
    /// Splits source text into line information for context display.
    /// </summary>
    private static LineInfo[]? SplitLines(ReadOnlySpan<char> source)
    {
        if (source.IsEmpty)
            return null;

        // Count lines first
        int lineCount = 1;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
                lineCount++;
        }

        var lines = ArrayPool<LineInfo>.Shared.Rent(lineCount);
        int lineIndex = 0;
        int lineStart = 0;

        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                var len = i - lineStart;
                // Strip \r if present
                if (len > 0 && source[lineStart + len - 1] == '\r')
                    len--;

                lines[lineIndex++] = new LineInfo(lineStart, len);
                lineStart = i + 1;
            }
        }

        // Last line (no trailing newline)
        if (lineStart <= source.Length)
        {
            var len = source.Length - lineStart;
            if (len > 0 && source[source.Length - 1] == '\r')
                len--;
            lines[lineIndex] = new LineInfo(lineStart, len);
        }

        return lines;
    }

    /// <summary>
    /// Information about a line in the source text.
    /// </summary>
    public readonly struct LineInfo
    {
        public readonly int Start;
        public readonly int Length;

        public LineInfo(int start, int length)
        {
            Start = start;
            Length = length;
        }
    }
}
