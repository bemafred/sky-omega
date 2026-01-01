// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SkyOmega.Mercury.Runtime.IO;

/// <summary>
/// Formats query results as ASCII tables for terminal display.
/// Transport-agnostic - no dependencies on Mercury core.
/// </summary>
public sealed class ResultTableFormatter
{
    private readonly TextWriter _writer;
    private readonly bool _useColor;
    private readonly int _maxColumnWidth;
    private readonly int _maxRows;

    // ANSI colors
    private const string Reset = "\x1b[0m";
    private const string Bold = "\x1b[1m";
    private const string Cyan = "\x1b[36m";
    private const string Green = "\x1b[32m";
    private const string Yellow = "\x1b[33m";
    private const string Gray = "\x1b[90m";
    private const string Magenta = "\x1b[35m";
    private const string Red = "\x1b[91m";

    // Box drawing characters
    private const char TopLeft = '┌';
    private const char TopRight = '┐';
    private const char BottomLeft = '└';
    private const char BottomRight = '┘';
    private const char Horizontal = '─';
    private const char Vertical = '│';
    private const char Cross = '┼';
    private const char TopTee = '┬';
    private const char BottomTee = '┴';
    private const char LeftTee = '├';
    private const char RightTee = '┤';

    /// <summary>
    /// Creates a table formatter.
    /// </summary>
    public ResultTableFormatter(
        TextWriter writer,
        bool useColor = true,
        int maxColumnWidth = 50,
        int maxRows = 0)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _useColor = useColor;
        _maxColumnWidth = maxColumnWidth;
        _maxRows = maxRows;
    }

    /// <summary>
    /// Creates a formatter writing to Console.Out with automatic color detection.
    /// </summary>
    public static ResultTableFormatter Console => new(
        System.Console.Out,
        useColor: !System.Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null);

    /// <summary>
    /// Formats SELECT query results as a table.
    /// </summary>
    public void FormatSelect(ExecutionResult result)
    {
        if (result.Variables == null || result.Variables.Length == 0)
        {
            WriteColored(Gray, "(no variables selected)");
            _writer.WriteLine();
            return;
        }

        if (result.Rows == null || result.Rows.Count == 0)
        {
            WriteColored(Gray, "(no results)");
            _writer.WriteLine();
            return;
        }

        var variables = result.Variables;
        var rows = result.Rows;

        // Calculate column widths
        var widths = CalculateColumnWidths(variables, rows);

        // Header
        WriteTopBorder(widths);
        WriteHeaderRow(variables, widths);
        WriteSeparator(widths);

        // Data rows
        var displayRows = _maxRows > 0 ? Math.Min(rows.Count, _maxRows) : rows.Count;
        for (int i = 0; i < displayRows; i++)
        {
            WriteDataRow(variables, rows[i], widths);
        }

        WriteBottomBorder(widths);

        // Summary
        WriteSummary(result, displayRows);
    }

    /// <summary>
    /// Formats ASK query result.
    /// </summary>
    public void FormatAsk(ExecutionResult result)
    {
        if (result.AskResult == true)
        {
            WriteColored(Green + Bold, "true");
        }
        else
        {
            WriteColored(Yellow + Bold, "false");
        }
        _writer.WriteLine();

        WriteTimingSummary(result);
    }

    /// <summary>
    /// Formats CONSTRUCT/DESCRIBE results as triples.
    /// </summary>
    public void FormatTriples(ExecutionResult result)
    {
        if (result.Triples == null || result.Triples.Count == 0)
        {
            WriteColored(Gray, "(no triples)");
            _writer.WriteLine();
            return;
        }

        var displayCount = _maxRows > 0 ? Math.Min(result.Triples.Count, _maxRows) : result.Triples.Count;

        for (int i = 0; i < displayCount; i++)
        {
            var (s, p, o) = result.Triples[i];
            WriteColored(Cyan, s);
            _writer.Write(' ');
            WriteColored(Magenta, p);
            _writer.Write(' ');
            WriteColored(Reset, o);
            _writer.WriteLine(" .");
        }

        if (result.Triples.Count > displayCount)
        {
            WriteColored(Gray, $"... and {result.Triples.Count - displayCount} more triples");
            _writer.WriteLine();
        }

        _writer.WriteLine();
        WriteColored(Gray, $"{result.Triples.Count} triple{(result.Triples.Count == 1 ? "" : "s")}");
        WriteTimingSummary(result, inline: true);
        _writer.WriteLine();
    }

    /// <summary>
    /// Formats UPDATE result.
    /// </summary>
    public void FormatUpdate(ExecutionResult result)
    {
        if (result.Success)
        {
            WriteColored(Green, "OK");
            _writer.Write($" - {result.AffectedCount} triple{(result.AffectedCount == 1 ? "" : "s")} affected");
        }
        else
        {
            WriteColored(Yellow, "FAILED");
            if (!string.IsNullOrEmpty(result.Message))
            {
                _writer.Write($" - {result.Message}");
            }
        }
        _writer.WriteLine();

        WriteTimingSummary(result);
    }

    /// <summary>
    /// Formats an error result.
    /// </summary>
    public void FormatError(ExecutionResult result)
    {
        WriteColored(Red, "Error: ");
        _writer.WriteLine(result.Message ?? "Unknown error");
    }

    private int[] CalculateColumnWidths(string[] variables, List<Dictionary<string, string>> rows)
    {
        var widths = new int[variables.Length];

        // Start with header widths
        for (int i = 0; i < variables.Length; i++)
        {
            widths[i] = Math.Min(variables[i].Length, _maxColumnWidth);
        }

        // Check data widths
        foreach (var row in rows)
        {
            for (int i = 0; i < variables.Length; i++)
            {
                if (row.TryGetValue(variables[i], out var value) && value != null)
                {
                    var displayValue = FormatValue(value);
                    widths[i] = Math.Max(widths[i], Math.Min(displayValue.Length, _maxColumnWidth));
                }
            }
        }

        return widths;
    }

    private void WriteTopBorder(int[] widths)
    {
        WriteColored(Gray, TopLeft.ToString());
        for (int i = 0; i < widths.Length; i++)
        {
            if (i > 0) WriteColored(Gray, TopTee.ToString());
            WriteColored(Gray, new string(Horizontal, widths[i] + 2));
        }
        WriteColored(Gray, TopRight.ToString());
        _writer.WriteLine();
    }

    private void WriteBottomBorder(int[] widths)
    {
        WriteColored(Gray, BottomLeft.ToString());
        for (int i = 0; i < widths.Length; i++)
        {
            if (i > 0) WriteColored(Gray, BottomTee.ToString());
            WriteColored(Gray, new string(Horizontal, widths[i] + 2));
        }
        WriteColored(Gray, BottomRight.ToString());
        _writer.WriteLine();
    }

    private void WriteSeparator(int[] widths)
    {
        WriteColored(Gray, LeftTee.ToString());
        for (int i = 0; i < widths.Length; i++)
        {
            if (i > 0) WriteColored(Gray, Cross.ToString());
            WriteColored(Gray, new string(Horizontal, widths[i] + 2));
        }
        WriteColored(Gray, RightTee.ToString());
        _writer.WriteLine();
    }

    private void WriteHeaderRow(string[] variables, int[] widths)
    {
        WriteColored(Gray, Vertical.ToString());
        for (int i = 0; i < variables.Length; i++)
        {
            _writer.Write(' ');
            var header = variables[i].PadRight(widths[i]);
            if (header.Length > widths[i])
                header = header[..(widths[i] - 1)] + "…";
            WriteColored(Bold + Cyan, header);
            _writer.Write(' ');
            WriteColored(Gray, Vertical.ToString());
        }
        _writer.WriteLine();
    }

    private void WriteDataRow(string[] variables, Dictionary<string, string> row, int[] widths)
    {
        WriteColored(Gray, Vertical.ToString());
        for (int i = 0; i < variables.Length; i++)
        {
            _writer.Write(' ');

            var value = row.TryGetValue(variables[i], out var v) ? FormatValue(v ?? "") : "";
            var color = GetValueColor(v ?? "");

            if (value.Length > widths[i])
                value = value[..(widths[i] - 1)] + "…";
            else
                value = value.PadRight(widths[i]);

            WriteColored(color, value);
            _writer.Write(' ');
            WriteColored(Gray, Vertical.ToString());
        }
        _writer.WriteLine();
    }

    private void WriteSummary(ExecutionResult result, int displayedRows)
    {
        var totalRows = result.Rows?.Count ?? 0;

        if (totalRows > displayedRows)
        {
            WriteColored(Gray, $"({displayedRows} of {totalRows} rows shown)");
            _writer.WriteLine();
        }

        WriteColored(Gray, $"{totalRows} row{(totalRows == 1 ? "" : "s")}");
        WriteTimingSummary(result, inline: true);
        _writer.WriteLine();
    }

    private void WriteTimingSummary(ExecutionResult result, bool inline = false)
    {
        if (!inline)
            _writer.WriteLine();

        WriteColored(Gray, " (");
        WriteColored(Gray, $"{result.ParseTime.TotalMilliseconds:F1}ms parse, ");
        WriteColored(Gray, $"{result.ExecutionTime.TotalMilliseconds:F1}ms exec");
        WriteColored(Gray, ")");
    }

    private static string FormatValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // Shorten long IRIs by showing prefix if possible
        if (value.StartsWith('<') && value.EndsWith('>'))
        {
            var iri = value[1..^1];
            var lastSlash = iri.LastIndexOf('/');
            var lastHash = iri.LastIndexOf('#');
            var splitPos = Math.Max(lastSlash, lastHash);
            if (splitPos > 0 && splitPos < iri.Length - 1)
            {
                var localName = iri[(splitPos + 1)..];
                if (localName.Length < iri.Length / 2)
                    return $"<…{localName}>";
            }
        }

        return value;
    }

    private static string GetValueColor(string value)
    {
        if (string.IsNullOrEmpty(value))
            return Gray;

        if (value.StartsWith('<'))
            return Cyan; // IRI

        if (value.StartsWith('_'))
            return Magenta; // Blank node

        if (value.StartsWith('"'))
            return Reset; // Literal

        return Reset;
    }

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
}
