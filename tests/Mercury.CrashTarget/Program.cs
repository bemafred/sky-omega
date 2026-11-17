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
//   states-first   ADR-064: a store of the profile <tag> names (Graph or Minimal). Single writes assert K, A and B, a
//                  batch asserts C, then a clean exit.
//   states-killed  ADR-064: on the store states-first left, single writes and two batches that make each kind of
//                  change of M4, and repeat some that change nothing, then the process is killed. <count> is not
//                  used by these two modes. See States below.
//
// Each step prints one line on stdout, so a test can tell how far the target got.

using System.Diagnostics;
using SkyOmega.Mercury.Abstractions;
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

if (mode is "states-first" or "states-killed")
{
    var states = new QuadStore(storePath, null, null, new StorageOptions { Profile = Enum.Parse<StoreProfile>(tag) });
    Say("opened");

    if (mode == "states-first")
    {
        states.AddCurrent(States.S, States.P, "\"K\"");                 // v1
        states.AddCurrent(States.S, States.P, "\"A\"");                 // v1
        states.AddCurrent(States.S, States.P, "\"B\"");                 // v1
        states.BeginBatch();
        states.AddCurrentBatched(States.S, States.P, "\"C\"");          // v1
        states.CommitBatch();
        Say("committed");
        states.Dispose();
        Say("disposed");
        return 0;
    }

    states.DeleteCurrent(States.S, States.P, "\"A\"");                  // v2
    states.AddCurrent(States.S, States.P, "\"A\"");                     // v3
    states.DeleteCurrent(States.S, States.P, "\"B\"");                  // v2
    states.DeleteCurrent(States.S, States.P, "\"B\"");                  // retracted already: nothing
    states.AddCurrent(States.S, States.P, "\"K\"");                     // asserted already: nothing
    states.BeginBatch();
    states.DeleteCurrentBatched(States.S, States.P, "\"C\"");           // v2
    states.AddCurrentBatched(States.S, States.P, "\"C\"");              // v3
    states.DeleteCurrentBatched(States.S, States.P, "\"C\"");           // v4
    states.CommitBatch();
    states.BeginBatch();
    states.AddCurrentBatched(States.S, States.P, "\"D\"");              // v1
    states.DeleteCurrentBatched(States.S, States.P, "\"D\"");           // v2
    states.AddCurrentBatched(States.S, States.P, "\"D\"");              // v3
    states.AddCurrentBatched(States.S, States.P, "\"D\"");              // asserted already: nothing
    states.CommitBatch();
    states.AddCurrent(States.S, States.P, "\"E\"");                     // v1
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

/// <summary>
/// What the two states modes write (ADR-064). FactStatesTests states the same subject and predicate to check the
/// reopened store.
/// </summary>
internal static class States
{
    public const string S = "<urn:states:s>";
    public const string P = "<urn:states:p>";
}

/// <summary>A clock whose reading the program sets. It stands still until the program moves it.</summary>
internal sealed class SuppliedClock : TimeProvider
{
    public DateTimeOffset Reading { get; set; }
    public override DateTimeOffset GetUtcNow() => Reading;
}
