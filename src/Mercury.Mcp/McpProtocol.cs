// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkyOmega.Mercury.Runtime.IO;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Execution;
using SkyOmega.Mercury.Sparql.Parsing;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Mcp;

/// <summary>
/// MCP (Model Context Protocol) implementation over stdin/stdout.
/// Exposes Mercury capabilities as MCP tools.
/// </summary>
/// <remarks>
/// Protocol: JSON-RPC 2.0 over newline-delimited JSON
/// Spec: https://modelcontextprotocol.io/
/// </remarks>
public static class McpProtocol
{
    private const string ProtocolVersion = "2024-11-05";

    /// <summary>
    /// Run the MCP protocol loop.
    /// </summary>
    public static async Task RunAsync(QuadStore store, Stream input, Stream output)
    {
        using var reader = new StreamReader(input, Encoding.UTF8);
        using var writer = new StreamWriter(output, Encoding.UTF8) { AutoFlush = true };

        var handler = new McpHandler(store);

        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var response = handler.HandleMessage(line);
                if (response != null)
                {
                    await writer.WriteLineAsync(response);
                }
            }
            catch (Exception ex)
            {
                // Write error response
                var errorResponse = CreateErrorResponse(null, -32603, $"Internal error: {ex.Message}");
                await writer.WriteLineAsync(errorResponse);
            }
        }
    }

    private static string CreateErrorResponse(object? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id != null ? JsonValue.Create(id) : null,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        return response.ToJsonString();
    }

    private sealed class McpHandler
    {
        private readonly QuadStore _store;
        private bool _initialized;

        public McpHandler(QuadStore store)
        {
            _store = store;
        }

        public string? HandleMessage(string json)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch
            {
                return CreateErrorResponse(null, -32700, "Parse error");
            }

            if (node is not JsonObject request)
                return CreateErrorResponse(null, -32600, "Invalid Request");

            var method = request["method"]?.GetValue<string>();
            var id = request["id"];
            var parameters = request["params"];

            // Notifications (no id) don't get responses
            var isNotification = id == null;

            var result = method switch
            {
                "initialize" => HandleInitialize(parameters),
                "initialized" => HandleInitialized(),
                "tools/list" => HandleToolsList(),
                "tools/call" => HandleToolsCall(parameters),
                "ping" => HandlePing(),
                _ => null
            };

            if (isNotification)
                return null;

            if (result == null)
            {
                return CreateErrorResponse(id, -32601, $"Method not found: {method}");
            }

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["result"] = result
            };

            return response.ToJsonString();
        }

        private JsonNode HandleInitialize(JsonNode? parameters)
        {
            _initialized = true;

            return new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject { }
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "mercury-mcp",
                    ["version"] = "1.0.0"
                }
            };
        }

        private JsonNode? HandleInitialized()
        {
            // Notification - no response
            return null;
        }

        private JsonNode HandlePing()
        {
            return new JsonObject { };
        }

        private JsonNode HandleToolsList()
        {
            return new JsonObject
            {
                ["tools"] = new JsonArray
                {
                    CreateToolDefinition(
                        "mercury_query",
                        "Execute a SPARQL SELECT, ASK, CONSTRUCT, or DESCRIBE query against the Mercury triple store",
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["query"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["description"] = "The SPARQL query to execute"
                                }
                            },
                            ["required"] = new JsonArray { "query" }
                        }),
                    CreateToolDefinition(
                        "mercury_update",
                        "Execute a SPARQL UPDATE (INSERT, DELETE, LOAD, CLEAR, etc.) to modify the triple store",
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["update"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["description"] = "The SPARQL UPDATE statement to execute"
                                }
                            },
                            ["required"] = new JsonArray { "update" }
                        }),
                    CreateToolDefinition(
                        "mercury_stats",
                        "Get Mercury store statistics (quad count, atoms, storage size, WAL status)",
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { }
                        }),
                    CreateToolDefinition(
                        "mercury_graphs",
                        "List all named graphs in the Mercury triple store",
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { }
                        })
                }
            };
        }

        private static JsonObject CreateToolDefinition(string name, string description, JsonObject inputSchema)
        {
            return new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["inputSchema"] = inputSchema
            };
        }

        private JsonNode HandleToolsCall(JsonNode? parameters)
        {
            var name = parameters?["name"]?.GetValue<string>();
            var args = parameters?["arguments"] as JsonObject;

            var (content, isError) = name switch
            {
                "mercury_query" => ExecuteQuery(args?["query"]?.GetValue<string>()),
                "mercury_update" => ExecuteUpdate(args?["update"]?.GetValue<string>()),
                "mercury_stats" => GetStats(),
                "mercury_graphs" => GetGraphs(),
                _ => ($"Unknown tool: {name}", true)
            };

            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = content
                    }
                },
                ["isError"] = isError
            };
        }

        private (string content, bool isError) ExecuteQuery(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return ("Query is required", true);

            try
            {
                var parser = new SparqlParser(query.AsSpan());
                Query parsed;

                try
                {
                    parsed = parser.ParseQuery();
                }
                catch (SparqlParseException ex)
                {
                    return ($"Error: {ex.Message}", true);
                }

                _store.AcquireReadLock();
                try
                {
                    var executor = new QueryExecutor(_store, query.AsSpan(), parsed);
                    var sb = new StringBuilder();

                    switch (parsed.Type)
                    {
                        case QueryType.Select:
                            var results = executor.Execute();
                            var rows = new List<string[]>();
                            string[]? varNames = null;

                            try
                            {
                                while (results.MoveNext())
                                {
                                    var bindings = results.Current;
                                    varNames ??= ExtractVariableNames(bindings, query);
                                    var row = new string[bindings.Count];
                                    for (int i = 0; i < bindings.Count; i++)
                                        row[i] = bindings.GetString(i).ToString();
                                    rows.Add(row);
                                }
                            }
                            finally
                            {
                                results.Dispose();
                            }

                            if (varNames != null && rows.Count > 0)
                            {
                                sb.AppendLine(string.Join("\t", varNames));
                                foreach (var row in rows)
                                    sb.AppendLine(string.Join("\t", row));
                                sb.AppendLine($"\n{rows.Count} result(s)");
                            }
                            else
                            {
                                sb.AppendLine("No results");
                            }
                            break;

                        case QueryType.Ask:
                            sb.AppendLine(executor.ExecuteAsk() ? "true" : "false");
                            break;

                        case QueryType.Construct:
                        case QueryType.Describe:
                            var tripleResults = executor.Execute();
                            var count = 0;
                            try
                            {
                                while (tripleResults.MoveNext())
                                {
                                    var bindings = tripleResults.Current;
                                    if (bindings.Count >= 3)
                                    {
                                        sb.AppendLine($"{bindings.GetString(0)} {bindings.GetString(1)} {bindings.GetString(2)} .");
                                        count++;
                                    }
                                }
                            }
                            finally
                            {
                                tripleResults.Dispose();
                            }
                            sb.AppendLine($"\n{count} triple(s)");
                            break;

                        default:
                            return ($"Unsupported query type: {parsed.Type}", true);
                    }

                    return (sb.ToString().Trim(), false);
                }
                finally
                {
                    _store.ReleaseReadLock();
                }
            }
            catch (Exception ex)
            {
                return ($"Error: {ex.Message}", true);
            }
        }

        private static string[] ExtractVariableNames(BindingTable bindings, string source)
        {
            var knownVars = new List<(string Name, int Hash)>();
            var span = source.AsSpan();

            for (int i = 0; i < span.Length - 1; i++)
            {
                if (span[i] == '?')
                {
                    int start = i;
                    int end = i + 1;
                    while (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] == '_'))
                        end++;

                    if (end > start + 1)
                    {
                        var varName = span.Slice(start, end - start).ToString();
                        uint hash = 2166136261;
                        foreach (var ch in varName)
                        {
                            hash ^= ch;
                            hash *= 16777619;
                        }
                        if (!knownVars.Exists(v => v.Hash == (int)hash))
                            knownVars.Add((varName, (int)hash));
                    }
                }
            }

            var result = new string[bindings.Count];
            for (int i = 0; i < bindings.Count; i++)
            {
                var bindingHash = bindings.GetVariableHash(i);
                var found = knownVars.Find(v => v.Hash == bindingHash);
                result[i] = found.Name ?? $"?var{i}";
            }

            return result;
        }

        private (string content, bool isError) ExecuteUpdate(string? update)
        {
            if (string.IsNullOrWhiteSpace(update))
                return ("Update statement is required", true);

            try
            {
                var parser = new SparqlParser(update.AsSpan());
                UpdateOperation parsed;

                try
                {
                    parsed = parser.ParseUpdate();
                }
                catch (SparqlParseException ex)
                {
                    return ($"Error: {ex.Message}", true);
                }

                var executor = new UpdateExecutor(_store, update.AsSpan(), parsed);
                var result = executor.Execute();

                if (!result.Success)
                    return ($"Error: {result.ErrorMessage}", true);

                return ($"OK - {result.AffectedCount} triple(s) affected", false);
            }
            catch (Exception ex)
            {
                return ($"Error: {ex.Message}", true);
            }
        }

        private (string content, bool isError) GetStats()
        {
            var (quadCount, atomCount, totalBytes) = _store.GetStatistics();
            var (walTxId, walCheckpoint, walSize) = _store.GetWalStatistics();

            var sb = new StringBuilder();
            sb.AppendLine("Mercury Store Statistics:");
            sb.AppendLine($"  Quads: {quadCount:N0}");
            sb.AppendLine($"  Atoms: {atomCount:N0}");
            sb.AppendLine($"  Storage: {FormatBytes(totalBytes)}");
            sb.AppendLine($"  WAL TxId: {walTxId:N0}");
            sb.AppendLine($"  WAL Checkpoint: {walCheckpoint:N0}");
            sb.AppendLine($"  WAL Size: {FormatBytes(walSize)}");

            return (sb.ToString().Trim(), false);
        }

        private (string content, bool isError) GetGraphs()
        {
            var graphs = new List<string>();

            _store.AcquireReadLock();
            try
            {
                var enumerator = _store.GetNamedGraphs();
                while (enumerator.MoveNext())
                    graphs.Add(enumerator.Current.ToString());
            }
            finally
            {
                _store.ReleaseReadLock();
            }

            if (graphs.Count == 0)
                return ("No named graphs. Only the default graph exists.", false);

            var sb = new StringBuilder();
            sb.AppendLine($"Named graphs ({graphs.Count}):");
            foreach (var g in graphs.OrderBy(g => g))
            {
                sb.AppendLine($"  <{g}>");
            }

            return (sb.ToString().Trim(), false);
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double size = bytes;
            int unit = 0;

            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0 ? $"{size:N0} {units[unit]}" : $"{size:N1} {units[unit]}";
        }
    }
}
