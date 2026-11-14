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
//   periods-first  ADR-061: one batch memorizes four quads at clock reading 1, then a clean exit.
//   periods-killed ADR-061: on the store periods-first left, single writes and one batch at clock readings 2
//                  to 6 that make each of the four decided changes (begin, end, remove, continue), then the
//                  process is killed. <tag> and <count> are not used by these two modes. See Periods below.
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

if (mode is "periods-first" or "periods-killed")
{
    var clock = new SuppliedClock { Reading = Periods.Reading(1) };
    var periods = new QuadStore(storePath, null, null, new StorageOptions { Clock = clock });
    Say("opened");

    if (mode == "periods-first")
    {
        periods.BeginBatch();
        periods.AddCurrentBatched(Periods.S, Periods.P, "\"K\"");
        periods.AddCurrentBatched(Periods.S, Periods.P, "\"A0\"");
        periods.AddCurrentBatched(Periods.S, Periods.P, "\"A1\"");
        periods.AddCurrentBatched(Periods.S, Periods.P, "\"C\"");
        periods.CommitBatch();
        Say("committed");
        periods.Dispose();
        Say("disposed");
        return 0;
    }

    clock.Reading = Periods.Reading(2);
    periods.DeleteCurrent(Periods.S, Periods.P, "\"A0\"");      // end
    periods.AddCurrent(Periods.S, Periods.P, "\"A0\"");         // continue: forgotten and memorized in reading 2
    clock.Reading = Periods.Reading(3);
    periods.DeleteCurrent(Periods.S, Periods.P, "\"A1\"");      // end
    clock.Reading = Periods.Reading(4);
    periods.AddCurrent(Periods.S, Periods.P, "\"B\"");          // begin
    periods.DeleteCurrent(Periods.S, Periods.P, "\"B\"");       // remove: memorized and forgotten in reading 4
    periods.AddCurrent(Periods.S, Periods.P, "\"A1\"");         // begin: a second period
    clock.Reading = Periods.Reading(5);
    periods.BeginBatch();
    periods.DeleteCurrentBatched(Periods.S, Periods.P, "\"C\""); // end
    periods.AddCurrentBatched(Periods.S, Periods.P, "\"D\"");    // begin
    periods.CommitBatch();
    clock.Reading = Periods.Reading(6);
    periods.DeleteCurrent(Periods.S, Periods.P, "\"D\"");       // end
    Say("changed");
    Kill();
    return 3; // not reached
}

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

/// <summary>
/// What the two periods modes write (ADR-061). MemoryTimeRecoveryTests states the same subject, predicate and clock
/// readings to check the reopened store.
/// </summary>
internal static class Periods
{
    public const string S = "<urn:periods:s>";
    public const string P = "<urn:periods:p>";

    /// <summary>Clock reading <paramref name="n"/>: n minutes after 2031-01-01T00:00:00Z.</summary>
    public static DateTimeOffset Reading(int n) => new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(n);
}

/// <summary>A clock whose reading the program sets. It stands still until the program moves it.</summary>
internal sealed class SuppliedClock : TimeProvider
{
    public DateTimeOffset Reading { get; set; }
    public override DateTimeOffset GetUtcNow() => Reading;
}
