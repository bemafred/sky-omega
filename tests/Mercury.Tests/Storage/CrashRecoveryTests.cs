using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SkyOmega.Mercury.Runtime;
using SkyOmega.Mercury.Storage;
using Xunit;

namespace SkyOmega.Mercury.Tests.Storage;

/// <summary>
/// ADR-058 D3: restart conformance. Each test lets a separate process (the crash target in
/// tests/Mercury.CrashTarget) write to a store and then kill itself, the way a crashed server or a
/// server killed by its MCP client ends. The test then reopens the store in this process and checks
/// that every acknowledged write is present with its own terms.
/// </summary>
/// <remarks>
/// The "crash" tests in <see cref="TransactionalIntegrityTests"/> close the store with Dispose, so they
/// never exercise the state a killed process leaves on disk. A real kill is the only faithful test: the
/// atom files are opened FileShare.None, and Dispose is the step under suspicion.
/// </remarks>
public class CrashRecoveryTests : IDisposable
{
    private readonly string _root;

    public CrashRecoveryTests()
    {
        var tempPath = TempPath.Test("crash-recovery");
        tempPath.MarkOwnership();
        _root = tempPath;
    }

    public void Dispose() => TempPath.SafeCleanup(_root);

    private string StorePath(string name = "store") => Path.Combine(_root, name);

    [Fact]
    public void Commit_ThenKilled_ReopenHasTheTriplesWithTheirTerms()
    {
        var store = StorePath();
        CrashTarget.Run("commit", store, "a", 3).AssertKilledAfter("committed");

        // D6: the commit made the atoms and their header durable, so the open-time walk
        // (D7) finds nothing past the header.
        using (var atoms = new HashAtomStore(Path.Combine(store, "atoms")))
        {
            Assert.Equal(0, atoms.AtomsRecoveredAtOpen);
            Assert.True(atoms.AtomCount >= 7, $"atom header says {atoms.AtomCount} atoms");
        }

        AssertTriples(store, ("a", 3));
    }

    [Fact]
    public void KilledTwice_TheSecondSessionDoesNotReuseAtomIds()
    {
        // elite's shape: a session is killed, the next session writes new terms and is killed too.
        // If the second session reuses the first one's atom IDs, the first session's quads resolve
        // to the second session's strings.
        var store = StorePath();
        CrashTarget.Run("commit", store, "first", 3).AssertKilledAfter("committed");
        CrashTarget.Run("commit", store, "second", 3).AssertKilledAfter("committed");

        AssertTriples(store, ("first", 3), ("second", 3));
    }

    [Fact]
    public void Killed_ThenACleanSession_KeepsTheEarlierTriples()
    {
        var store = StorePath();
        CrashTarget.Run("commit", store, "killed", 3).AssertKilledAfter("committed");
        CrashTarget.Run("clean", store, "clean", 3).AssertExitedCleanly();

        AssertTriples(store, ("killed", 3), ("clean", 3));
    }

    [Fact]
    public void Uncommitted_ThenKilled_LeavesNothingOfTheBatch()
    {
        var store = StorePath();
        CrashTarget.Run("commit", store, "kept", 2).AssertKilledAfter("committed");
        CrashTarget.Run("uncommitted", store, "dropped", 2).AssertKilledAfter("added");

        AssertTriples(store, ("kept", 2));
        AssertAbsent(store, "dropped", 2);
    }

    [Fact]
    public void ManySessions_EachKilled_KeepEveryBatch()
    {
        // Batch sizes up to 2000 make the B+trees allocate pages and split, so the index headers
        // (root, next page, entry count) change between sessions.
        var store = StorePath();
        var batches = new List<(string Tag, int Count)>();
        int[] sizes = { 1, 7, 50, 2000, 3, 400, 1, 120, 9, 900 };
        for (var i = 0; i < sizes.Length; i++)
        {
            var tag = $"batch{i}";
            CrashTarget.Run("commit", store, tag, sizes[i]).AssertKilledAfter("committed");
            batches.Add((tag, sizes[i]));
        }

        AssertTriples(store, batches.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(30)]
    public void KilledInsideDispose_KeepsTheCommittedBatch(int delayMs)
    {
        // The Claude Code shutdown: Dispose starts, and the process is killed shortly after.
        // Where the kill lands depends on the delay and the machine; every landing must be safe.
        var store = StorePath();
        CrashTarget.Run("commit", store, "before", 5).AssertKilledAfter("committed");
        CrashTarget.Run("dispose-killed", store, "during", 5, delayMs).AssertSawStep("committed");

        AssertTriples(store, ("before", 5), ("during", 5));
    }

    // ---- assertions over a reopened store ----

    private static void AssertTriples(string storePath, params (string Tag, int Count)[] batches)
    {
        using var store = new QuadStore(storePath);
        long expected = 0;
        foreach (var (tag, count) in batches)
        {
            expected += count;
            for (var i = 0; i < count; i++)
            {
                var (s, p, o, g) = Triples.Get(tag, i);
                var found = Query(store, s, g);
                Assert.True(found.Count == 1,
                    $"{s} in {g}: expected 1 triple, found {found.Count} [{string.Join("; ", found)}]");
                Assert.Equal($"{s} {p} {o}", found[0]);
            }
        }

        var (quads, atoms, _) = store.GetStatistics();
        Assert.Equal(expected, quads);
        Assert.True(atoms > 0, "the reopened atom store reports no atoms");
    }

    private static void AssertAbsent(string storePath, string tag, int count)
    {
        using var store = new QuadStore(storePath);
        for (var i = 0; i < count; i++)
        {
            var (s, _, _, g) = Triples.Get(tag, i);
            Assert.Empty(Query(store, s, g));
        }
    }

    private static List<string> Query(QuadStore store, string subject, string graph)
    {
        var found = new List<string>();
        store.AcquireReadLock();
        try
        {
            var results = store.QueryCurrent(subject, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, graph);
            try
            {
                while (results.MoveNext())
                {
                    var q = results.Current;
                    found.Add($"{q.Subject.ToString()} {q.Predicate.ToString()} {q.Object.ToString()}");
                }
            }
            finally
            {
                results.Dispose();
            }
        }
        finally
        {
            store.ReleaseReadLock();
        }
        return found;
    }

    /// <summary>The triples the crash target writes (tests/Mercury.CrashTarget/Program.cs, class Triples).</summary>
    private static class Triples
    {
        public static (string S, string P, string O, string G) Get(string tag, int i) =>
            ($"<urn:crash:{tag}:s{i}>", "<urn:crash:p>", $"\"{tag} value {i}\"", $"<urn:crash:g:{tag}>");
    }
}

/// <summary>Runs the ADR-058 crash target as a separate process.</summary>
internal static class CrashTarget
{
    public sealed record Result(int ExitCode, IReadOnlyList<string> Steps, string Stderr)
    {
        /// <summary>The target reached <paramref name="step"/> and was then killed.</summary>
        public void AssertKilledAfter(string step)
        {
            AssertSawStep(step);
            Assert.True(ExitCode != 0, $"expected the target to be killed after '{step}', but it exited {ExitCode}");
        }

        /// <summary>The target reached <paramref name="step"/>; whether it was killed or finished is up to timing.</summary>
        public void AssertSawStep(string step) =>
            Assert.True(Steps.Contains(step),
                $"crash target never reached '{step}' (exit {ExitCode}, steps [{string.Join(", ", Steps)}], stderr: {Stderr})");

        public void AssertExitedCleanly() =>
            Assert.True(ExitCode == 0 && Steps.Contains("disposed"),
                $"expected a clean exit (exit {ExitCode}, steps [{string.Join(", ", Steps)}], stderr: {Stderr})");
    }

    public static Result Run(string mode, string storePath, string tag, int count, int delayMs = 0)
    {
        var psi = new ProcessStartInfo(DotnetHost())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(DllPath());
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(storePath);
        psi.ArgumentList.Add(tag);
        psi.ArgumentList.Add(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(delayMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("could not start the crash target");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill();
            throw new TimeoutException($"crash target did not end within 60 s (mode {mode})");
        }
        var steps = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new Result(process.ExitCode, steps, stderrTask.Result);
    }

    /// <summary>The crash target is built next to this test project with the same configuration and TFM.</summary>
    private static string DllPath()
    {
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory);   // .../Mercury.Tests/bin/<config>/<tfm>/
        var tfm = tfmDir.Name;
        var config = tfmDir.Parent?.Name ?? "Debug";
        var testsDir = tfmDir.Parent?.Parent?.Parent?.Parent
            ?? throw new InvalidOperationException($"cannot find tests/ above {AppContext.BaseDirectory}");
        var dll = Path.Combine(testsDir.FullName, "Mercury.CrashTarget", "bin", config, tfm, "MercuryCrashTarget.dll");
        Assert.True(File.Exists(dll), $"crash target not built: {dll}");
        return dll;
    }

    /// <summary>The dotnet host of the runtime running the tests, so the target runs on the same runtime.</summary>
    private static string DotnetHost()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();     // <root>/shared/Microsoft.NETCore.App/<ver>/
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtimeDir.TrimEnd(Path.DirectorySeparatorChar))));
        var name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        if (root is not null)
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate)) return candidate;
        }
        return name;
    }
}
