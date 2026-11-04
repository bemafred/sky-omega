// Crash target for the ADR-058 restart tests.
//
// Usage: MercuryCrashTarget <mode> <storePath> <tag> <count> [delayMs]
//
// Every mode writes <count> triples tagged <tag> (see Triples below) and then ends the process.
// The modes differ in where the process ends:
//
//   commit         CommitBatch returns, then the process is killed. The state a crashed or
//                  killed server leaves after an acknowledged write.
//   uncommitted    The batch is written to the WAL but never committed, then the process is
//                  killed. Nothing of it may be visible after reopen.
//   dispose-killed CommitBatch returns, Dispose starts, and a timer kills the process <delayMs>
//                  later, usually inside Dispose (the Claude Code SIGINT -> SIGTERM case).
//   clean          CommitBatch, then Dispose, then a normal exit. The control.
//
// The kill is Process.Kill on the current process: SIGKILL on Unix, TerminateProcess on Windows.
// Nothing runs after it: no Dispose, no finalizers, no flush of managed buffers.
//
// Each step prints one line on stdout, so a test can tell how far the target got.

using System.Diagnostics;
using SkyOmega.Mercury.Storage;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: MercuryCrashTarget <commit|uncommitted|dispose-killed|clean> <storePath> <tag> <count> [delayMs]");
    return 2;
}

var mode = args[0];
var storePath = args[1];
var tag = args[2];
var count = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
var delayMs = args.Length > 4 ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 0;

var store = new QuadStore(storePath);
Say("opened");

store.BeginBatch();
for (var i = 0; i < count; i++)
{
    var (s, p, o, g) = Triples.Get(tag, i);
    store.AddCurrentBatched(s, p, o, g);
}
Say("added");

switch (mode)
{
    case "uncommitted":
        Kill();
        break;

    case "commit":
        store.CommitBatch();
        Say("committed");
        Kill();
        break;

    case "dispose-killed":
        store.CommitBatch();
        Say("committed");
        var killer = new Thread(() =>
        {
            Thread.Sleep(delayMs);
            Kill();
        }) { IsBackground = true };
        killer.Start();
        store.Dispose();
        Say("disposed");
        Kill();
        break;

    case "clean":
        store.CommitBatch();
        Say("committed");
        store.Dispose();
        Say("disposed");
        return 0;

    default:
        Console.Error.WriteLine($"unknown mode {mode}");
        return 2;
}

return 3; // not reached: Kill does not return

static void Say(string step)
{
    Console.Out.WriteLine(step);
    Console.Out.Flush();
}

static void Kill() => Process.GetCurrentProcess().Kill();

/// <summary>The triples a run writes. CrashRecoveryTests computes the same strings to check them.</summary>
internal static class Triples
{
    public static (string S, string P, string O, string G) Get(string tag, int i) =>
        ($"<urn:crash:{tag}:s{i}>", "<urn:crash:p>", $"\"{tag} value {i}\"", $"<urn:crash:g:{tag}>");
}
