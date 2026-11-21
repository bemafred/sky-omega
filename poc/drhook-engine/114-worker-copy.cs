#!/usr/bin/env -S dotnet
#:property TargetFramework=net10.0
#:project ../../src/DrHook.Wire/DrHook.Wire.csproj
//
// DrHook.Engine probe 114 — what a worker's own copy of the build costs (ADR-016 6.7's design) ========================
//
// ADR-016 6.7's design (accepted 2026-11-21): at each start the supervisor copies the worker's directory into DrHook's
// directory and runs the worker from the copy, so that a rebuild cannot replace the files of a running worker. "To
// measure first: what the copy costs, on finding 113's three machines. If it is not small beside the start-up, the copy
// is made once per build instead." Finding 113 did not time a copy.
//
// Construction: the worker's directory does not exist yet. It will hold the engine and its dependencies, without the
// MCP SDK and the Generic Host, so drhook-mcp's own build output (Debug, as run from source) is an upper bound on its
// files and bytes. The probe builds drhook-mcp, then copies that output N times into DrHook's directory (the directory of
// the rendezvous socket, WireRendezvous), each copy into a directory of its own, the way the supervisor would: create
// each directory, File.Copy each file. Each copy is timed, then checked (the same files and bytes as the source, untimed),
// then deleted, and the delete is timed. Copy 1 is reported apart.
//
// Small, set before the run: a copy is small beside the start-up when its median is at most the median start-up a
// worker per session adds on that machine, finding 113, Debug: omega 95 ms, omega-ubuntu 50 ms, omega-windows 128 ms.
// That is, the copy would at most double what a worker per session adds.
//
// Hypothesis: the copy is small on every machine. The files are in the page cache after the build, and a copy moves
// about 36 MB in about 90 files. Refuted on a machine where the copy's median is larger than that start-up.
//
// RESULT(0) when every copy held the source's files and bytes and was deleted; 1 otherwise; 2 when the build failed or
// the destination already existed.
//
// Usage (from poc/drhook-engine, in a clone that no running drhook-mcp runs from, since it builds src/DrHook.Mcp):
//   dotnet run --no-cache 114-worker-copy.cs [copies]
//   dotnet run --no-cache 114-worker-copy.cs 20

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SkyOmega.DrHook.Wire;

// Every number prints the same way on every machine (at dbd336d the per-copy lines followed omega-windows' Swedish culture).
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

int copies = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 20;
if (copies < 2) { Console.WriteLine("copies must be at least 2"); return 1; }

string project = Path.GetFullPath(Path.Combine("..", "..", "src", "DrHook.Mcp", "DrHook.Mcp.csproj"));
string? source = BuildOutput(project);
if (source is null) { Console.WriteLine("FAIL(2): the build of drhook-mcp failed"); return 2; }

string drhookDirectory = Path.GetDirectoryName(WireRendezvous.DefaultSocketPath())!;
string destinationRoot = Path.Combine(drhookDirectory, $"probe114-{Environment.ProcessId}");
if (Directory.Exists(destinationRoot)) { Console.WriteLine($"FAIL(2): {destinationRoot} already exists"); return 2; }
Directory.CreateDirectory(destinationRoot);

BuildContents sourceContents = BuildContents.Read(source);
Console.WriteLine($"platform: {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} cores, {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"source: {source}");
Console.WriteLine($"  {sourceContents.Files} files, {sourceContents.Directories} directories, {sourceContents.Bytes:N0} bytes");
Console.WriteLine($"destination: {destinationRoot}");
Console.WriteLine($"{copies} copies");

var copyMs = new List<double>();
var deleteMs = new List<double>();
bool everyCopyComplete = true;
for (int copy = 1; copy <= copies; copy++)
{
    string destination = Path.Combine(destinationRoot, copy.ToString(CultureInfo.InvariantCulture));
    var clock = Stopwatch.StartNew();
    CopyBuild(source, destination);
    double copied = clock.Elapsed.TotalMilliseconds;

    BuildContents copiedContents = BuildContents.Read(destination);
    bool complete = copiedContents == sourceContents;
    if (!complete) everyCopyComplete = false;

    clock.Restart();
    Directory.Delete(destination, recursive: true);
    double deleted = clock.Elapsed.TotalMilliseconds;

    copyMs.Add(copied);
    deleteMs.Add(deleted);
    Console.WriteLine($"  copy {copy}: copy {copied:0.0} ms, delete {deleted:0.0} ms" +
                      (complete ? "" : $"  INCOMPLETE: {copiedContents.Files} files, {copiedContents.Bytes:N0} bytes"));
}
Directory.Delete(destinationRoot);

Console.WriteLine();
Console.WriteLine($"== copies 2 to {copies} (median [min–max] ms)");
Console.WriteLine($"   copy     {Distribution.Spread(copyMs.Skip(1))}");
Console.WriteLine($"   delete   {Distribution.Spread(deleteMs.Skip(1))}");
Console.WriteLine($"   copy 1   copy {copyMs[0]:0.0}, delete {deleteMs[0]:0.0}");
Console.WriteLine();
Console.WriteLine(everyCopyComplete
    ? "RESULT(0): every copy held the source's files and bytes, and was deleted"
    : "RESULT(1): a copy did not hold the source's files and bytes (see above)");
return everyCopyComplete ? 0 : 1;

// Build drhook-mcp (Debug) and return its output directory, or null when the build fails.
static string? BuildOutput(string project)
{
    var build = new ProcessStartInfo("dotnet", $"build \"{project}\" -c Debug")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using (Process p = Process.Start(build)!)
    {
        string output = p.StandardOutput.ReadToEnd();
        string errors = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) { Console.WriteLine(output); Console.WriteLine(errors); return null; }
    }

    var property = new ProcessStartInfo("dotnet", $"msbuild \"{project}\" -p:Configuration=Debug -getProperty:TargetDir")
    { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    using (Process p = Process.Start(property)!)
    {
        string targetDir = p.StandardOutput.ReadToEnd().Trim();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 && Directory.Exists(targetDir) ? targetDir : null;
    }
}

// Copy a build's directory the way the supervisor would: each directory created, each file copied.
static void CopyBuild(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.EnumerateFiles(source))
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    foreach (string directory in Directory.EnumerateDirectories(source))
        CopyBuild(directory, Path.Combine(destination, Path.GetFileName(directory)));
}

readonly record struct BuildContents(int Files, int Directories, long Bytes)
{
    public static BuildContents Read(string directory)
    {
        var root = new DirectoryInfo(directory);
        FileInfo[] files = root.GetFiles("*", SearchOption.AllDirectories);
        int directories = root.GetDirectories("*", SearchOption.AllDirectories).Length;
        return new BuildContents(files.Length, directories, files.Sum(f => f.Length));
    }
}

static class Distribution
{
    public static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return double.NaN;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    public static string Spread(IEnumerable<double> values)
    {
        double[] all = values.ToArray();
        if (all.Length == 0) return "none";
        return string.Create(CultureInfo.InvariantCulture, $"{Median(all):0.0} [{all.Min():0.0}–{all.Max():0.0}]");
    }
}
