// DrHook probe 108 — how does Rider's "Attach to child .NET processes" hold a child? (ADR-017 Step 2, Q9) ============
//
// ADR-017's lead candidate for holding a child is the runtime's diagnostics-port pause (finding 107). Rider has caught
// child .NET processes since 2025.1, but does not document how. Rider is DrHook's reference for correctness, so this
// probe watches it do so. It changes nothing in Rider or DrHook; it is a program that tells what it sees, plus a
// read-only script (../108-rider-child-hold-read.sh) for what the program cannot see itself.
//
// The program: a supervisor starts worker 1 with Process.Start; worker 1 starts worker 2 (Rider claims grandchildren
// too); each waits for its child and ends naturally. Every process writes, to bin/<config>/net10.0/marks/<run>/
// <pid>-<role>.txt and to the console:
//   modinit   ticks and Debugger.IsAttached in the [ModuleInitializer], the first managed code of this assembly
//   main      the top of Main
//   facts     pid, process path, runtime, Debugger.IsAttached, the environment (values only for DOTNET_, COMPlus_ and
//             CORECLR_ variables; names only for the rest, so no secret is written), and the loaded assemblies
//   start-called / start-returned   around Process.Start, in the parent: does Rider hold the parent too?
//   checkpoint   the worker's breakpoint line (see below)
// Times are UTC ticks, so marks from different processes can be ordered.
//
// Runs (each a separate marks/<run>/ directory, named by the supervisor's start time):
//   A  no debugger: the built apphost started from a terminal (sky-omega runs it). The baseline: environment, timing.
//   B  Rider, Debug, a .NET Project run configuration, "Attach to child .NET processes" OFF, the breakpoint set.
//      What Rider gives the process it launches. The workers are not debugged, so the breakpoint does not hit.
//   C  the same with the option ON. Each worker should stop at the breakpoint. While worker 1 is stopped, sky-omega runs
//      the read script; then continue (worker 2 stops; read again if wanted; continue).
// Rider steps for B and C: open RiderChildHold.csproj (it is outside SkyOmega.sln); Run > Edit Configurations > add
// .NET Project, project RiderChildHold; tick or untick "Attach to child .NET processes"; set a breakpoint on the line
// marked BREAKPOINT below; Debug.
//
// Hypotheses (stated before any run; the probe is built to tell them apart):
//   H1 diagnostics port: in C the workers' environment holds DOTNET_DiagnosticPorts (with suspend), absent in A and B,
//      and the read script shows the worker's socket connected to one bound by a JetBrains process.
//   H2 startup hook: in C the supervisor's environment holds DOTNET_STARTUP_HOOKS naming a JetBrains assembly, which is
//      in its loaded assemblies, absent in B; a hook in the parent can catch Process.Start itself.
//   H3 neither variable differs between B and C. Then the timing and the read script show what holds the child.
//   For all: in C, Debugger.IsAttached true at the worker's modinit means Rider was attached before the worker's own
//   managed code ran, and start-called -> worker modinit longer than in A means the child was held.
//   No basis yet to rank H1 against H2.
//
// Ends: every process ends naturally. A parent waits for its child without a bound, because a worker held at a
// breakpoint is the scenario: the supervisor ends after its workers, as a supervisor does. Rider's Stop ends a run.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

string role = args.Length > 0 ? args[0] : "supervisor";

if (role == "supervisor")
{
    string dir = Path.Combine(AppContext.BaseDirectory, "marks",
        DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(dir);
    var log = new MarkLog(dir, "supervisor");
    log.Write("modinit", ModuleInit.Ticks, $"attached={ModuleInit.Attached}");
    log.Mark("main");
    log.Facts();
    int workerCode = Children.Start(log, 1, dir);
    log.Mark($"exit (worker 1 exit {workerCode})");
    return 0;
}

if (role == "worker" && args.Length == 3)
{
    int depth = int.Parse(args[1], CultureInfo.InvariantCulture);
    string dir = args[2];
    var log = new MarkLog(dir, $"worker{depth}");
    log.Write("modinit", ModuleInit.Ticks, $"attached={ModuleInit.Attached}");
    log.Mark("main");
    log.Facts();
    int checkpoint = depth * 10;                                   // BREAKPOINT: set the Rider breakpoint on this line
    log.Mark($"checkpoint {checkpoint} attached={Debugger.IsAttached}");
    int childCode = depth < 2 ? Children.Start(log, depth + 1, dir) : 0;
    log.Mark($"exit (child exit {childCode})");
    return childCode == 0 ? 0 : 3;
}

Console.Error.WriteLine("usage: RiderChildHold                (supervisor)");
Console.Error.WriteLine("       RiderChildHold worker <depth> <marks dir>");
return 2;

static class Children
{
    // Starts this program again as a worker, through the same host this process runs under (the apphost, or `dotnet`
    // with the dll), and waits for it.
    public static int Start(MarkLog log, int depth, string dir)
    {
        string self = Environment.ProcessPath!;
        var psi = new ProcessStartInfo(self) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(self) == "dotnet")
            psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RiderChildHold.dll"));
        psi.ArgumentList.Add("worker");
        psi.ArgumentList.Add(depth.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(dir);

        log.Mark($"start-called worker{depth}");
        using Process child = Process.Start(psi)!;
        log.Mark($"start-returned worker{depth} pid {child.Id}");
        child.WaitForExit();
        log.Mark($"child-ended worker{depth} exit {child.ExitCode}");
        return child.ExitCode;
    }
}

sealed class MarkLog(string dir, string role)
{
    readonly string _path = Path.Combine(dir, $"{Environment.ProcessId}-{role}.txt");

    public void Mark(string name) => Write(name, DateTime.UtcNow.Ticks, "");

    public void Write(string name, long ticks, string detail)
    {
        string time = new DateTime(ticks, DateTimeKind.Utc).ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        Line($"{name} {ticks} {time}{(detail.Length > 0 ? " " + detail : "")}");
    }

    public void Facts()
    {
        Line($"pid {Environment.ProcessId}");
        Line($"process-path {Environment.ProcessPath}");
        Line($"runtime {RuntimeInformation.FrameworkDescription} {RuntimeInformation.RuntimeIdentifier}");
        Line($"attached-now {Debugger.IsAttached}");
        var names = Environment.GetEnvironmentVariables().Keys.Cast<string>().Order(StringComparer.Ordinal);
        foreach (string name in names)
        {
            bool runtimeVariable = name.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase);
            Line(runtimeVariable ? $"env-value {name}={Environment.GetEnvironmentVariable(name)}" : $"env-name {name}");
        }
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.FullName, StringComparer.Ordinal))
            Line($"assembly {assembly.GetName().Name} {(assembly.IsDynamic ? "(dynamic)" : assembly.Location)}");
    }

    public void Line(string text)
    {
        string line = $"[{Environment.ProcessId} {role}] {text}";
        Console.WriteLine(line);
        Console.Out.Flush();
        File.AppendAllText(_path, line + Environment.NewLine);
    }
}

static class ModuleInit
{
    public static long Ticks;
    public static bool Attached;

    [ModuleInitializer]
    internal static void Run()
    {
        Ticks = DateTime.UtcNow.Ticks;
        Attached = Debugger.IsAttached;
    }
}
