#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
//
// DrHook.Engine probe 101 — platform facts, read-only (ADR-016 Step 1) ==============================================
//
// Question: on this machine, which debugger code does DrHook load, how does mscordbi read a target's memory, and what
// is the environment? On Linux also: what can the debugger process read about its own threads without ptrace rights?
// The no-capture constructions of Steps 2 to 4 depend on that answer (Step 0: no outside debugger or sampler on Linux).
//
// Why memory reads matter: finding 100's hang is a read over the debugger transport. mscordbi decides the path once,
// when it attaches, in the debugger process (release/10.0 425ba4b, pal/src/debug/debug.cpp, PAL_OpenProcessMemory):
// macOS reads directly when task_for_pid(mach_task_self(), pid) returns KERN_SUCCESS, Linux when
// open("/proc/<pid>/mem", O_RDONLY) succeeds; otherwise every read goes over the transport. Windows has no transport.
// This probe makes the same call from the same process while attached, and releases what it gets.
//
// Why two targets on Unix: with ptrace_scope=1, Linux lets a process open the memory of its descendants only. DrHook's
// tests start their own targets, which are descendants; a program DrHook attaches to in use usually is not. So the
// probe attaches once to a descendant (started under bash, which waits for it; finding 87) and once to a reparented
// target (started under bash, which exits at once).
//
// Expectation, to be checked and not assumed: on Linux the descendant is read directly and the reparented target over
// the transport; on macOS both go over the transport unless task_for_pid is allowed (probe 100 on omega saw a read wait
// for a frozen target). If that holds, the Linux CI tests never take the transport path, which would explain why finding
// 100's cascade has appeared on macOS only.
//
// Construction: one child process per target, bounded at 120 s from outside (as probe 100). The child
//   1. records its threads (Linux) and the debugger images it has loaded, before touching DrHook;
//   2. starts the target and records its parent and whether it descends from the child;
//   3. attaches with DebugSession.Attach (borrowed: DrHook does not own the target's end) and waits until no debugger
//      event has arrived for 500 ms (at most 10 s), so the attach-time events are done;
//   4. records the memory-open result, the debugger images (path, size, sha256), the debug pipes, and (Linux) each
//      thread's comm, state, wchan, syscall and stack, or the error reading them;
//   5. ends the session with Dispose, the borrowed path (synchronize, then detach), and records the threads again;
//   6. waits for the target to end by itself: it writes a marker file when its idle time is over.
// Nothing is stopped, evaluated, signalled or killed by the child. The parent kills a child or a target only when the
// bound expires, and reports that.
//
// The target runs under the same dotnet as the probe, so the debugger never mixes runtime versions (finding 86).
//
// Output: a report on stdout; with --out <file>, the same facts as JSON for comparing machines.
// Result codes: 0 every run recorded and every target ended by itself; 1 otherwise; 2 target build failed.
//
// Usage (from poc/drhook-engine):
//   dotnet run --no-cache 101-platform-facts-smoke.cs [--out facts.json]
//   DOTNET_ROOT=$HOME/.dotnet ~/.dotnet/dotnet run --no-cache 101-platform-facts-smoke.cs   (a second .NET install)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using SkyOmega.DrHook.Engine;

if (args.Length == 4 && args[0] == "child") return Child(args[1], args[2], args[3]);

string? outFile = args.Length == 2 && args[0] == "--out" ? Path.GetFullPath(args[1]) : null;
string runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
string host = DotnetHostOf(runtimeDirectory);
string? dll = BuildTarget(host, Path.GetFullPath("101-platform-facts-target.cs"));
if (dll is null) { Console.WriteLine("FAIL(2): could not build 101-platform-facts-target.cs"); return 2; }

JsonObject environment = EnvironmentFacts(host, runtimeDirectory);
Console.WriteLine("environment:");
foreach (var (key, value) in environment) Console.WriteLine($"  {key}: {value?.ToJsonString().Trim('"')}");

var runs = new JsonArray();
bool allRecorded = true;
foreach (string mode in OperatingSystem.IsWindows() ? new[] { "started" } : new[] { "descendant", "reparented" })
{
    var psi = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in new[] { "child", mode, host, dll }) psi.ArgumentList.Add(a);
    using Process child = Process.Start(psi)!;
    var errors = new List<string>();
    child.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.Add(e.Data); };
    child.BeginErrorReadLine();

    var lines = new List<string>();
    string result = "child-timeout";
    string? facts = null;
    var reader = new Thread(() =>
    {
        string? line;
        while ((line = child.StandardOutput.ReadLine()) is not null)
        {
            if (line.StartsWith("child: facts=", StringComparison.Ordinal)) { facts = line["child: facts=".Length..]; continue; }
            lock (lines) lines.Add(line);
            if (line.StartsWith("child: result=", StringComparison.Ordinal)) { result = line["child: result=".Length..].Trim(); return; }
        }
    }) { IsBackground = true };
    reader.Start();
    reader.Join(TimeSpan.FromSeconds(120));
    if (!child.WaitForExit(5000)) child.Kill(entireProcessTree: true);
    lock (lines)
    {
        // A target still alive when its run was not recorded is ended here, and the run says so. Only a process that is
        // still the dotnet host is ended, so a reused PID is never touched.
        if (result != "recorded")
            foreach (string line in lines.Where(l => l.StartsWith("child: target=", StringComparison.Ordinal)))
                try
                {
                    using Process leftover = Process.GetProcessById(int.Parse(line.Split('=')[1].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                    if (leftover.ProcessName.StartsWith("dotnet", StringComparison.OrdinalIgnoreCase))
                    {
                        leftover.Kill();
                        result += " (target killed by the parent after the run)";
                    }
                }
                catch (ArgumentException) { }
        Console.WriteLine($"{mode}: {result}");
        foreach (string line in lines.Where(l => l.StartsWith("child:", StringComparison.Ordinal) && !l.StartsWith("child: result=", StringComparison.Ordinal)))
            Console.WriteLine($"    {line["child: ".Length..]}");
        if (result != "recorded")
            lock (errors) foreach (string line in errors) Console.WriteLine($"    stderr: {line}");
    }
    allRecorded &= result == "recorded";
    runs.Add(new JsonObject { ["mode"] = mode, ["result"] = result, ["facts"] = facts is null ? null : JsonNode.Parse(facts) });
}

if (outFile is not null)
{
    var report = new JsonObject { ["probe"] = "101-platform-facts", ["recordedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture), ["environment"] = environment, ["runs"] = runs };
    File.WriteAllText(outFile, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"facts written to {outFile}");
}
Console.WriteLine(allRecorded
    ? "RESULT(0): every run recorded; every target ended by itself after the detach"
    : "RESULT(1): a run was not recorded in full (see above)");
return allRecorded ? 0 : 1;

// ---- the child: one target, one attach, one clean detach ----------------------------------------------------------

static int Child(string mode, string host, string dll)
{
    var clock = Stopwatch.StartNew();
    var facts = new JsonObject();
    void Say(string text) { Console.WriteLine($"child: {text} [{clock.ElapsedMilliseconds} ms]"); Console.Out.Flush(); }
    int Result(string outcome)
    {
        Console.WriteLine($"child: facts={facts.ToJsonString()}");
        Console.WriteLine($"child: result={outcome}");
        Console.Out.Flush();
        return 0;
    }

    facts["beforeAttach"] = new JsonObject { ["debuggerImages"] = DebuggerImages(), ["threads"] = OwnThreads() };

    string doneMarker = Path.Combine(Path.GetTempPath(), $"probe101-done-{System.Environment.ProcessId}-{mode}");
    File.Delete(doneMarker);
    const int idleSeconds = 20;
    (int pid, Process? waitable) = StartTarget(mode, host, dll, idleSeconds, doneMarker);
    Console.WriteLine($"child: target={pid}");
    Console.Out.Flush();
    facts["target"] = new JsonObject { ["pid"] = pid, ["mode"] = mode, ["ancestry"] = Ancestry(pid) };
    Say($"target {pid} started ({mode})");

    var sink = new Sink();
    DebugSession session;
    try { session = DebugSession.Attach(pid, sink); }
    catch (Exception e) { Say($"attach failed: {e.GetType().Name}: {e.Message}"); facts["attachError"] = $"{e.GetType().Name}: {e.Message}"; return Result("attach-failed"); }
    long attachedAt = clock.ElapsedMilliseconds;
    while (sink.QuietMilliseconds < 500 && clock.ElapsedMilliseconds - attachedAt < 10_000)
        Thread.Sleep(50);
    Say($"attached; {sink.Count} debugger events, quiet for 500 ms");

    facts["attached"] = new JsonObject
    {
        ["eventsDuringAttach"] = sink.Histogram(),
        ["memoryOpen"] = MemoryOpen(pid),
        ["debuggerImages"] = DebuggerImages(),
        ["debugPipes"] = DebugPipes(pid),
        ["threads"] = OwnThreads(),
        ["anomalies"] = sink.Anomalies(),
    };
    Say($"memory open: {facts["attached"]!["memoryOpen"]!.ToJsonString()}");

    try { session.Dispose(); }
    catch (Exception e) { Say($"detach threw {e.GetType().Name}: {e.Message}"); facts["detachError"] = $"{e.GetType().Name}: {e.Message}"; }
    Thread.Sleep(500);
    facts["afterDetach"] = new JsonObject { ["threads"] = OwnThreads(), ["debugPipes"] = DebugPipes(pid), ["anomalies"] = sink.Anomalies() };
    Say("session disposed (borrowed: synchronize, then detach)");

    // The target ends by itself when its idle time is over; it writes the marker just before it returns.
    var waited = Stopwatch.StartNew();
    bool ended = false;
    while (waited.Elapsed < TimeSpan.FromSeconds(idleSeconds + 15))
    {
        if (waitable is not null ? waitable.HasExited : !IsAlive(pid)) { ended = true; break; }
        Thread.Sleep(200);
    }
    bool marker = File.Exists(doneMarker);
    File.Delete(doneMarker);
    facts["targetEnd"] = new JsonObject
    {
        ["ended"] = ended,
        ["doneMarker"] = marker,
        ["exitCode"] = ended && waitable is not null ? waitable.ExitCode : null,
        ["waitedMs"] = waited.ElapsedMilliseconds,
    };
    Say(ended ? $"target ended; done marker {(marker ? "written" : "missing")}" : "target still alive after its idle time and 15 s");
    return Result(ended && marker && facts["detachError"] is null ? "recorded" : ended ? "ended-without-marker-or-detach-error" : "target-did-not-end");
}

// Descendant: bash starts the target and waits for it, so the target is bash's child and the child's grandchild, and
// bash's exit code is the target's (finding 87: not a direct child of the debugger process). Reparented: bash starts it
// and exits at once, so the target is handed to init or the nearest subreaper. Windows: a direct child.
static (int Pid, Process? Waitable) StartTarget(string mode, string host, string dll, int idleSeconds, string doneMarker)
{
    if (OperatingSystem.IsWindows())
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (string a in new[] { dll, idleSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), doneMarker }) start.ArgumentList.Add(a);
        Process target = Process.Start(start)!;
        target.StandardOutput.ReadLine();
        return (target.Id, target);
    }
    var psi = new ProcessStartInfo("bash") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add(mode == "descendant"
        ? "\"$0\" \"$1\" \"$2\" \"$3\" >/dev/null 2>&1 </dev/null & echo $!; wait $!"
        : "\"$0\" \"$1\" \"$2\" \"$3\" >/dev/null 2>&1 </dev/null & echo $!");
    foreach (string a in new[] { host, dll, idleSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), doneMarker }) psi.ArgumentList.Add(a);
    Process shell = Process.Start(psi)!;
    int pid = int.Parse(shell.StandardOutput.ReadLine()!.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    if (mode == "reparented") shell.WaitForExit();
    Thread.Sleep(1000); // the attach waits for the target's runtime itself (finding 99); this only lets main start
    return (pid, mode == "descendant" ? shell : null);
}

// ---- the facts ----------------------------------------------------------------------------------------------------

// The call mscordbi makes when it attaches (PAL_OpenProcessMemory), from the same process; what it opens is released.
static JsonObject MemoryOpen(int pid)
{
    if (OperatingSystem.IsMacOS())
    {
        uint self = MachTaskSelf();
        int kr = task_for_pid(self, pid, out uint port);
        if (kr == 0) mach_port_deallocate(self, port);
        return new JsonObject { ["call"] = "task_for_pid", ["kern_return"] = kr, ["reads"] = kr == 0 ? "direct" : "transport" };
    }
    if (OperatingSystem.IsLinux())
    {
        int fd = open($"/proc/{pid}/mem", 0);
        int errno = fd == -1 ? Marshal.GetLastPInvokeError() : 0;
        if (fd != -1) close(fd);
        return new JsonObject
        {
            ["call"] = $"open(/proc/{pid}/mem, O_RDONLY)",
            ["errno"] = errno,
            ["error"] = errno == 0 ? null : Marshal.GetPInvokeErrorMessage(errno),
            ["reads"] = fd != -1 ? "direct" : "transport",
        };
    }
    IntPtr handle = OpenProcess(0x0010 | 0x1000, false, pid); // PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION
    int error = handle == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0;
    if (handle != IntPtr.Zero) CloseHandle(handle);
    return new JsonObject { ["call"] = "OpenProcess(PROCESS_VM_READ)", ["error"] = error, ["reads"] = "direct (Windows has no debugger transport)" };
}

// The debugger's native code in this process: dbgshim, mscordbi, the DAC, and this process's own coreclr.
static JsonArray DebuggerImages()
{
    IEnumerable<string> paths;
    if (OperatingSystem.IsLinux())
        paths = File.ReadAllLines("/proc/self/maps").Select(l => l.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries)).Select(f => f.Length == 6 ? f[5].Trim() : "").Where(p => p.StartsWith('/'));
    else if (OperatingSystem.IsMacOS())
        paths = Enumerable.Range(0, (int)_dyld_image_count()).Select(i => Marshal.PtrToStringUTF8(_dyld_get_image_name((uint)i)) ?? "");
    else
    {
        using Process self = Process.GetCurrentProcess();
        paths = self.Modules.Cast<ProcessModule>().Select(m => m.FileName ?? "").ToList();
    }
    var images = new JsonArray();
    foreach (string path in paths.Distinct().Where(p => new[] { "dbgshim", "mscordbi", "mscordaccore", "coreclr" }.Any(n => Path.GetFileName(p).Contains(n, StringComparison.OrdinalIgnoreCase))).OrderBy(p => p, StringComparer.Ordinal))
    {
        var info = new FileInfo(path);
        images.Add(new JsonObject
        {
            ["path"] = path,
            ["bytes"] = info.Exists ? info.Length : null,
            ["sha256"] = info.Exists ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : null,
        });
    }
    return images;
}

// The runtime names its debug transport pipes clr-debug-pipe-<pid>-<key>-in/out and its diagnostic socket
// dotnet-diagnostic-<pid>-<key>-socket, in its temp directory.
static JsonArray DebugPipes(int pid)
{
    var names = new JsonArray();
    if (OperatingSystem.IsWindows()) return names;
    foreach (string entry in Directory.EnumerateFileSystemEntries(Path.GetTempPath())
        .Where(e => Path.GetFileName(e).StartsWith($"clr-debug-pipe-{pid}-", StringComparison.Ordinal) || Path.GetFileName(e).StartsWith($"dotnet-diagnostic-{pid}-", StringComparison.Ordinal))
        .OrderBy(e => e, StringComparer.Ordinal))
        names.Add(Path.GetFileName(entry));
    return names;
}

// Linux: what this process can read of each of its own threads, with no ptrace rights beyond its own.
static JsonArray OwnThreads()
{
    var threads = new JsonArray();
    if (!OperatingSystem.IsLinux()) return threads;
    foreach (string task in Directory.EnumerateDirectories("/proc/self/task").OrderBy(t => int.Parse(Path.GetFileName(t), System.Globalization.CultureInfo.InvariantCulture)))
    {
        var thread = new JsonObject { ["tid"] = int.Parse(Path.GetFileName(task), System.Globalization.CultureInfo.InvariantCulture) };
        foreach (string file in new[] { "comm", "stat", "wchan", "syscall", "stack" })
        {
            try
            {
                string text = File.ReadAllText(Path.Combine(task, file)).Trim();
                thread[file] = file switch
                {
                    "stat" => text[(text.LastIndexOf(')') + 2)..].Split(' ')[0], // the state letter, after the comm
                    "syscall" => text.Split(' ')[0],                              // the syscall number, "running" or -1
                    "stack" => text.Split('\n').Length + " frames",
                    _ => text,
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { thread[file] = $"unreadable: {e.GetType().Name}: {e.Message}"; }
        }
        threads.Add(thread);
    }
    return threads;
}

// The target's parents up to the root, and whether this process is among them.
static JsonObject Ancestry(int pid)
{
    var chain = new JsonArray();
    int current = pid;
    for (int depth = 0; depth < 32 && current > 1; depth++)
    {
        int parent = ParentOf(current);
        if (parent <= 0) break;
        chain.Add(parent);
        current = parent;
    }
    bool descends = chain.Any(p => p!.GetValue<int>() == System.Environment.ProcessId);
    return new JsonObject { ["parents"] = chain, ["descendsFromDebugger"] = OperatingSystem.IsWindows() ? null : descends };
}

static int ParentOf(int pid)
{
    if (OperatingSystem.IsLinux())
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (IOException) { return -1; }
    }
    if (OperatingSystem.IsMacOS())
    {
        string text = Run("ps", "-o", "ppid=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)).Trim();
        return int.TryParse(text, out int parent) ? parent : -1;
    }
    return -1;
}

static bool IsAlive(int pid) => kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != 3; // 3 = ESRCH

// ---- the environment, recorded once by the parent -----------------------------------------------------------------

static JsonObject EnvironmentFacts(string host, string runtimeDirectory)
{
    var e = new JsonObject
    {
        ["machine"] = System.Environment.MachineName,
        ["os"] = RuntimeInformation.OSDescription,
        ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
        ["cores"] = System.Environment.ProcessorCount,
        ["memoryBytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
        ["probeRuntime"] = RuntimeInformation.FrameworkDescription,
        ["runtimeDirectory"] = runtimeDirectory,
        ["dotnetHost"] = host,
        ["runtimes"] = Run(host, "--list-runtimes").Trim(),
        ["DOTNET_ROOT"] = System.Environment.GetEnvironmentVariable("DOTNET_ROOT"),
        ["tempDirectory"] = Path.GetTempPath(),
        ["commit"] = Run("git", "rev-parse", "HEAD").Trim(),
        ["ci"] = string.Join(' ', new[] { "GITHUB_ACTIONS", "RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion" }
            .Select(v => (v, System.Environment.GetEnvironmentVariable(v))).Where(p => p.Item2 is not null).Select(p => $"{p.v}={p.Item2}")),
    };
    if (OperatingSystem.IsMacOS())
    {
        e["kernel"] = Run("uname", "-a").Trim();
        e["macOS"] = Run("sw_vers").Trim();
        e["virtualMachine"] = Run("sysctl", "-n", "kern.hv_vmm_present").Trim();
        e["developerMode"] = Run("DevToolsSecurity", "-status").Trim();
        e["systemIntegrityProtection"] = Run("csrutil", "status").Trim();
        e["hostEntitlements"] = Run("codesign", "-d", "--entitlements", "-", host).Trim();
    }
    if (OperatingSystem.IsLinux())
    {
        e["kernel"] = Run("uname", "-a").Trim();
        e["distribution"] = File.Exists("/etc/os-release") ? File.ReadAllLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal)) : null;
        e["ptraceScope"] = File.Exists("/proc/sys/kernel/yama/ptrace_scope") ? File.ReadAllText("/proc/sys/kernel/yama/ptrace_scope").Trim() : "no Yama";
        e["binSh"] = Run("readlink", "-f", "/bin/sh").Trim();
        e["virtualization"] = Run("systemd-detect-virt").Trim();
        e["libunwindLinked"] = string.Join(' ', Run("ldd", Path.Combine(runtimeDirectory, "libcoreclr.so")).Split('\n').Where(l => l.Contains("unwind", StringComparison.Ordinal)).Select(l => l.Trim()));
        e["user"] = Run("id").Trim();
    }
    if (OperatingSystem.IsWindows())
        e["windows"] = System.Environment.OSVersion.VersionString;
    return e;
}

// ---- support ------------------------------------------------------------------------------------------------------

// The dotnet that runs this probe: <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet.
static string DotnetHostOf(string runtimeDirectory)
{
    string root = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", ".."));
    string candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    return File.Exists(candidate) ? candidate : "dotnet";
}

// A read-only command, bounded at 10 s; its output, or the reason there is none.
static string Run(string file, params string[] arguments)
{
    try
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10_000)) { p.Kill(); return "(no answer in 10 s)"; }
        return stdout.Result.Length > 0 ? stdout.Result : stderr.Result;
    }
    catch (Exception e) { return $"(not run: {e.GetType().Name}: {e.Message})"; }
}

static string? BuildTarget(string host, string csPath)
{
    var psi = new ProcessStartInfo(host) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string a in new[] { "build", csPath, "-c", "Debug" }) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) { Console.Error.WriteLine(output); return null; }
    foreach (var line in output.Split('\n'))
    {
        int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
        if (arrow < 0) continue;
        string path = line[(arrow + 3)..].Trim();
        if (path.EndsWith(".dll", StringComparison.Ordinal) && File.Exists(path)) return path;
    }
    return null;
}

static uint MachTaskSelf()
{
    IntPtr libSystem = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
    return (uint)Marshal.ReadInt32(NativeLibrary.GetExport(libSystem, "mach_task_self_"));
}

[DllImport("/usr/lib/libSystem.B.dylib")] static extern int task_for_pid(uint targetTport, int pid, out uint task);
[DllImport("/usr/lib/libSystem.B.dylib")] static extern int mach_port_deallocate(uint task, uint name);
[DllImport("/usr/lib/libSystem.B.dylib")] static extern uint _dyld_image_count();
[DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr _dyld_get_image_name(uint index);
[DllImport("libc", SetLastError = true)] static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
[DllImport("libc")] static extern int close(int fd);
[DllImport("libc", SetLastError = true)] static extern int kill(int pid, int sig);
[DllImport("kernel32", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
[DllImport("kernel32")] static extern bool CloseHandle(IntPtr handle);

// Counts the debugger's events and keeps its anomalies; nothing here acts on the target.
sealed class Sink : IDebugEventSink
{
    private readonly Dictionary<string, int> _events = new();
    private readonly List<string> _anomalies = new();
    private long _lastEventTicks = Stopwatch.GetTimestamp();
    public int Count { get { lock (_events) return _events.Values.Sum(); } }
    public void OnEvent(string name) { lock (_events) { _events[name] = _events.GetValueOrDefault(name) + 1; _lastEventTicks = Stopwatch.GetTimestamp(); } }
    public void OnAnomaly(EngineAnomaly a) { lock (_anomalies) _anomalies.Add($"{a.Kind} in {a.Operation}: {a.Observed}"); }
    public long QuietMilliseconds { get { lock (_events) return (long)Stopwatch.GetElapsedTime(_lastEventTicks).TotalMilliseconds; } }
    public JsonObject Histogram() { lock (_events) return new JsonObject(_events.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))); }
    public JsonArray Anomalies() { lock (_anomalies) return new JsonArray(_anomalies.Select(a => (JsonNode?)a).ToArray()); }
}
