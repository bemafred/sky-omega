// drhook-mcp's journal (ADR-016 6.9): append-only, one JSON object per line, bounded at 4 MB and then one older file.
// No debugger runs here; the test lives with the worker's tests because this is the suite that references drhook-mcp.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkyOmega.DrHook.Mcp;

namespace DrHook.Engine.IntegrationTests;

[TestClass]
public sealed class JournalTest
{
    [TestMethod]
    public void AnEntry_IsAppendedAsOneLine_AndAJournalAtItsBound_IsMovedToTheOlderFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"drhook-journal-test-{Guid.NewGuid():N}.ndjson");
        try
        {
            var journal = new Journal(path);
            string first = journal.Write("drhook_kill", new JsonObject { ["workerPid"] = 1, ["reason"] = "first" });
            string second = journal.Write("discard", new JsonObject { ["workerPid"] = 2, ["reason"] = "second" });
            Assert.AreSequenceEqual(new[] { first, second }, File.ReadAllLines(path), "Each entry is appended as the line Write returned.");
            JsonNode entry = JsonNode.Parse(first)!;
            Assert.AreEqual("drhook_kill", (string?)entry["entry"]);
            Assert.AreEqual(Environment.ProcessId, (int)entry["drhookMcpPid"]!);
            DateTimeOffset.Parse((string)entry["at"]!, System.Globalization.CultureInfo.InvariantCulture); // a time, as written
            Assert.IsFalse(first.Contains("\\u002B", StringComparison.Ordinal), $"The time's + is written as itself: {first}");

            // At its bound the journal is moved to the older file, which it replaces, and the entry starts a new one.
            File.WriteAllText(path, new string('x', (int)Journal.Bound));
            string third = journal.Write("discard", new JsonObject { ["workerPid"] = 3 });
            Assert.AreEqual(Journal.Bound, new FileInfo(path + ".1").Length, "The full journal is the older file.");
            Assert.AreEqual(third, File.ReadAllLines(path).Single());
        }
        finally
        {
            foreach (string file in new[] { path, path + ".1", path + ".lock" })
                try { File.Delete(file); } catch (IOException) { /* best effort */ }
        }
    }
}
