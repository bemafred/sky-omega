// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using SkyOmega.Mercury.Diagnostics;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Repl;

/// <summary>
/// Interactive REPL runner with readline-style input handling.
/// </summary>
public sealed class InteractiveRepl : IDisposable
{
    private readonly ReplSession _session;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly DiagnosticFormatter _diagnosticFormatter;
    private readonly ResultTableFormatter _tableFormatter;
    private readonly bool _interactive;
    private bool _disposed;

    private const string Prompt = "mercury> ";
    private const string ContinuationPrompt = "      -> ";

    /// <summary>
    /// Creates an interactive REPL connected to a store.
    /// </summary>
    public InteractiveRepl(QuadStore store, TextReader? input = null, TextWriter? output = null)
    {
        _session = new ReplSession(store);
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
        _interactive = input == null && !Console.IsInputRedirected;

        var useColor = output == null && !Console.IsOutputRedirected &&
                       Environment.GetEnvironmentVariable("NO_COLOR") == null;

        _diagnosticFormatter = new DiagnosticFormatter(_output, useColor, "query");
        _tableFormatter = new ResultTableFormatter(_output, useColor);
    }

    /// <summary>
    /// Gets the underlying session.
    /// </summary>
    public ReplSession Session => _session;

    /// <summary>
    /// Runs the REPL loop until exit.
    /// </summary>
    public void Run()
    {
        PrintBanner();

        while (true)
        {
            var input = ReadInput();
            if (input == null)
                break; // EOF

            if (string.IsNullOrWhiteSpace(input))
                continue;

            var result = _session.Execute(input);
            PrintResult(result);

            // Check for exit command
            if (result.Kind == ExecutionResultKind.Command && result.Message == "EXIT")
                break;
        }

        if (_interactive)
        {
            _output.WriteLine();
            _output.WriteLine("Goodbye!");
        }
    }

    /// <summary>
    /// Executes a single command and returns the result.
    /// </summary>
    public ExecutionResult Execute(string input)
    {
        return _session.Execute(input);
    }

    private void PrintBanner()
    {
        if (!_interactive)
            return;

        _output.WriteLine("Mercury SPARQL REPL");
        _output.WriteLine("Type :help for commands, :quit to exit");
        _output.WriteLine();
    }

    private string? ReadInput()
    {
        if (_interactive)
        {
            _output.Write(Prompt);
        }

        var firstLine = _input.ReadLine();
        if (firstLine == null)
            return null;

        // Check if we need multi-line input
        if (!NeedsMoreInput(firstLine))
            return firstLine;

        // Multi-line mode
        var sb = new StringBuilder();
        sb.AppendLine(firstLine);

        while (true)
        {
            if (_interactive)
            {
                _output.Write(ContinuationPrompt);
            }

            var line = _input.ReadLine();
            if (line == null)
                break;

            sb.AppendLine(line);

            // Empty line or semicolon ends multi-line input
            if (string.IsNullOrWhiteSpace(line) || line.TrimEnd().EndsWith(';'))
                break;

            // Check if complete
            var current = sb.ToString();
            if (!NeedsMoreInput(current))
                break;
        }

        return sb.ToString().TrimEnd();
    }

    private static bool NeedsMoreInput(string input)
    {
        var trimmed = input.Trim();

        // Commands are single-line
        if (trimmed.StartsWith(':'))
            return false;

        // PREFIX and BASE are single-line
        if (trimmed.StartsWith("PREFIX", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("BASE", StringComparison.OrdinalIgnoreCase))
            return false;

        // Check for unclosed braces
        int braceCount = 0;
        int parenCount = 0;
        bool inString = false;
        bool inIri = false;

        for (int i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];

            if (inString)
            {
                if (c == '"' && (i == 0 || trimmed[i - 1] != '\\'))
                    inString = false;
                continue;
            }

            if (inIri)
            {
                if (c == '>')
                    inIri = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '<':
                    inIri = true;
                    break;
                case '{':
                    braceCount++;
                    break;
                case '}':
                    braceCount--;
                    break;
                case '(':
                    parenCount++;
                    break;
                case ')':
                    parenCount--;
                    break;
            }
        }

        // Unclosed delimiters need more input
        if (braceCount > 0 || parenCount > 0 || inString || inIri)
            return true;

        return false;
    }

    private void PrintResult(ExecutionResult result)
    {
        // Print diagnostics first
        if (result.HasDiagnostics)
        {
            foreach (var diag in result.Diagnostics!)
            {
                var color = diag.Severity switch
                {
                    DiagnosticSeverity.Error => "\x1b[91m",
                    DiagnosticSeverity.Warning => "\x1b[93m",
                    _ => "\x1b[96m"
                };
                var reset = "\x1b[0m";

                if (_output == Console.Out && !Console.IsOutputRedirected)
                {
                    _output.Write(color);
                    _output.Write(diag.CodeString);
                    _output.Write(reset);
                    _output.Write(": ");
                    _output.WriteLine(diag.Message);
                }
                else
                {
                    _output.WriteLine($"{diag.CodeString}: {diag.Message}");
                }
            }
        }

        switch (result.Kind)
        {
            case ExecutionResultKind.Empty:
                // Nothing to print
                break;

            case ExecutionResultKind.Select:
                _tableFormatter.FormatSelect(result);
                break;

            case ExecutionResultKind.Ask:
                _tableFormatter.FormatAsk(result);
                break;

            case ExecutionResultKind.Construct:
            case ExecutionResultKind.Describe:
                _tableFormatter.FormatTriples(result);
                break;

            case ExecutionResultKind.Update:
                _tableFormatter.FormatUpdate(result);
                break;

            case ExecutionResultKind.PrefixRegistered:
            case ExecutionResultKind.BaseSet:
            case ExecutionResultKind.Command:
                if (!string.IsNullOrEmpty(result.Message) && result.Message != "EXIT")
                {
                    _output.WriteLine(result.Message);
                }
                break;

            case ExecutionResultKind.Error:
                PrintError(result);
                break;
        }
    }

    private void PrintError(ExecutionResult result)
    {
        var useColor = _output == Console.Out && !Console.IsOutputRedirected;
        var red = useColor ? "\x1b[91m" : "";
        var reset = useColor ? "\x1b[0m" : "";

        _output.Write(red);
        _output.Write("Error: ");
        _output.Write(reset);
        _output.WriteLine(result.Message ?? "Unknown error");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _session.Dispose();
        _disposed = true;
    }
}
