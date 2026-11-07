// The diagnostics results' JSON in a host without reflection-based serialization. This project disables it
// (JsonSerializerIsReflectionEnabledByDefault=false), as trimmed, AOT and file-based hosts of the engine do; before
// 2026-11-07 both ToJson methods threw InvalidOperationException there (probe 106 dogfooding).

using System.Text.Json.Nodes;
using SkyOmega.DrHook.Engine.Diagnostics;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

public sealed class DiagnosticsJsonTests
{
    [Fact]
    public void ProcessList_ToJson_WritesEveryEntry_WithoutReflection()
    {
        var list = new DotNetProcessList([
            new DotNetProcessEntry(41, "first", "/bin/first", "1.2.3"),
            new DotNetProcessEntry(42, "second", "/bin/second", "unknown", ExecutableDeleted: true),
        ]);

        JsonNode json = JsonNode.Parse(list.ToJson())!;

        Assert.Equal(2, (int)json["count"]!);
        JsonArray processes = json["processes"]!.AsArray();
        Assert.Equal(41, (int)processes[0]!["pid"]!);
        Assert.Equal("first", (string)processes[0]!["name"]!);
        Assert.Equal("/bin/first", (string)processes[0]!["path"]!);
        Assert.Equal("1.2.3", (string)processes[0]!["version"]!);
        Assert.False((bool)processes[0]!["executableDeleted"]!);
        Assert.Equal(42, (int)processes[1]!["pid"]!);
        Assert.True((bool)processes[1]!["executableDeleted"]!);
    }

    // The JSON's reader is the LLM operator, over an MCP text channel, never HTML. The default encoder writes
    // + < > & ' and " as +, < and the like, which that reader has to decode in its head. Before
    // 2026-11-07 only the snapshot escaped relaxed; every other result escaped by default.
    [Fact]
    public void ProcessList_ToJson_WritesTheOperatorsCharactersAsThemselves()
    {
        var list = new DotNetProcessList([
            new DotNetProcessEntry(41, "Probe+Nested<T>", "/src/A & B/it's \"here\"", "1.0.0"),
        ]);

        string text = list.ToJson();

        Assert.Contains("\"name\": \"Probe+Nested<T>\"", text);
        Assert.Contains("\"path\": \"/src/A & B/it's \\\"here\\\"\"", text);
        Assert.DoesNotContain("\\u00", text);
    }

    [Fact]
    public void ObservationSnapshot_ToJson_WritesSummaryThreadsStacksExceptionsAndAnomalies_WithoutReflection()
    {
        var capturedAt = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = new ObservationSnapshot(
            Pid: 7,
            ProcessName: "target",
            AssemblyVersion: "1.0.0",
            CapturedAt: capturedAt,
            DurationMs: 500,
            Hypothesis: "the target is idle",
            ThreadCount: 3,
            TotalSamples: 150,
            Ticks: 50,
            ThreadSummary: [
                new ThreadSummary(12, 50, 2, 50, [new CollapsedStack(["System.Threading.Monitor.Wait(class System.Object,int32)", "Worker.Run()"], 50, 2)]),
                new ThreadSummary(11, 50, 45, 50, [
                    new CollapsedStack(["Spinner.Spin()", "Spinner.Run()"], 40, 40),
                    new CollapsedStack(["Spinner.Report()", "Spinner.Run()"], 10, 5)]),
                new ThreadSummary(13, 50, 2, 50, [new CollapsedStack(["System.Threading.Monitor.Wait(class System.Object,int32)", "Worker.Run()"], 50, 2)]),
            ],
            Exceptions: [new ExceptionEvent(capturedAt, "System.InvalidOperationException", "boom", 11)],
            GcEvents: [new GcEvent(capturedAt, 0, "AllocSmall")],
            ContentionCount: 3,
            Anomalies: ["CONTENTION: 3 lock contention events — possible deadlock risk"]);

        JsonNode json = JsonNode.Parse(snapshot.ToJson())!;

        Assert.Equal(7, (int)json["pid"]!);
        Assert.Equal("target", (string)json["processName"]!);
        Assert.Equal(capturedAt.ToString("O"), (string)json["capturedAt"]!);
        Assert.Equal("the target is idle", (string)json["hypothesis"]!);
        Assert.Equal(3, (int)json["summary"]!["threadCount"]!);
        Assert.Equal(150, (int)json["summary"]!["totalSamples"]!);
        Assert.Equal(50, (int)json["summary"]!["ticks"]!);
        Assert.Equal(1, (int)json["summary"]!["exceptionCount"]!);
        Assert.Equal(1, (int)json["summary"]!["gcEventCount"]!);
        Assert.Equal(3, (int)json["summary"]!["contentionCount"]!);
        Assert.Equal(11, (int)json["threads"]![0]!["threadId"]!);
        Assert.Equal(50, (int)json["threads"]![0]!["samples"]!);
        Assert.Equal(45, (int)json["threads"]![0]!["managedSamples"]!);
        Assert.Equal(90.0, (double)json["threads"]![0]!["managedPercentage"]!);
        Assert.Equal("running managed code", (string)json["threads"]![0]!["state"]!);
        Assert.Equal("Spinner.Spin()", (string)json["threads"]![0]!["at"]!);
        Assert.Equal("[0,1]", json["threads"]![0]!["stacks"]!.ToJsonString());
        Assert.Equal(12, (int)json["threads"]![1]!["threadId"]!);
        Assert.Equal("outside managed code", (string)json["threads"]![1]!["state"]!);
        Assert.Equal("System.Threading.Monitor.Wait(class System.Object,int32)", (string)json["threads"]![1]!["at"]!);
        Assert.Equal("[2]", json["threads"]![1]!["stacks"]!.ToJsonString());
        Assert.Equal(13, (int)json["threads"]![2]!["threadId"]!);
        Assert.Equal("[2]", json["threads"]![2]!["stacks"]!.ToJsonString());
        Assert.Equal(0, (int)json["threadsOmitted"]!);

        // The two waiting threads share one stack: it is collapsed into one entry naming both.
        JsonArray stacks = json["stacks"]!.AsArray();
        Assert.Equal(3, stacks.Count);
        Assert.Equal(40, (int)stacks[0]!["samples"]!);
        Assert.Equal("[\"Spinner.Spin()\",\"Spinner.Run()\"]", stacks[0]!["frames"]!.ToJsonString());
        Assert.Equal(100, (int)stacks[2]!["samples"]!);
        Assert.Equal(4, (int)stacks[2]!["managedSamples"]!);
        Assert.Equal("[12,13]", stacks[2]!["threads"]!.ToJsonString());
        Assert.Equal(0, (int)stacks[2]!["framesOmitted"]!);
        Assert.Equal("boom", (string)json["exceptions"]![0]!["message"]!);
        Assert.Equal("CONTENTION: 3 lock contention events — possible deadlock risk", (string)json["anomalies"]![0]!);
        Assert.StartsWith("Anomalies detected.", (string)json["deltaPrompt"]!);
    }
}
