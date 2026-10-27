using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SkyOmega.DrHook.Engine.Interop;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

/// <summary>The Windows launch spawn (probe 64): argument quoting, the environment block, and a held, redirected
/// child. Quoting and the environment block are pure and run everywhere; the spawn itself runs on Windows.</summary>
public sealed class WindowsSpawnTests
{
    public static TheoryData<string, string> QuotedArguments => new()
    {
        { "plain", "plain" },
        { "", "\"\"" },
        { @"C:\Program Files\dotnet\dotnet.exe", "\"C:\\Program Files\\dotnet\\dotnet.exe\"" },
        { "a\"b", "\"a\\\"b\"" },
        { @"a\""b", "\"a\\\\\\\"b\"" },
        { @"C:\my dir\", "\"C:\\my dir\\\\\"" },
        { @"C:\no\spaces\", @"C:\no\spaces\" },
    };

    [Theory]
    [MemberData(nameof(QuotedArguments))]
    public void CommandLine_QuotesEachArgument_ByTheWindowsParsingRules(string argument, string expected)
    {
        string commandLine = WindowsSpawn.CommandLine("prog", new[] { argument });

        Assert.Equal("prog " + expected, commandLine);

        // On Windows, the child's own parser is the oracle: it must give back exactly the argument.
        if (OperatingSystem.IsWindows())
        {
            nint argv = CommandLineToArgvW(commandLine, out int count);
            Assert.NotEqual(0, argv);
            var parsed = new string[count];
            for (int i = 0; i < count; i++)
                parsed[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * nint.Size))!;
            LocalFree(argv);
            Assert.Equal(new[] { "prog", argument }, parsed);
        }
    }

    [Fact]
    public void EnvironmentBlock_OverrideReplacesAnEntryOfAnyCase_AndEndsWithTwoNulls()
    {
        string existing = Environment.GetEnvironmentVariables().Keys.Cast<string>().First();
        string differentCase = existing.ToLowerInvariant() == existing ? existing.ToUpperInvariant() : existing.ToLowerInvariant();

        string block = WindowsSpawn.EnvironmentBlock(new Dictionary<string, string> { [differentCase] = "DRHOOK_OVERRIDDEN" });

        Assert.EndsWith("\0\0", block);
        string[] entries = block.TrimEnd('\0').Split('\0');
        string[] forName = entries.Where(e => e.StartsWith(existing + "=", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(new[] { $"{differentCase}=DRHOOK_OVERRIDDEN" }, forName);
    }

    [Fact]
    public void SpawnedChild_IsHeldUntilResume_SeesTheEnvOverride_AndWritesToTheReturnedPipe()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WindowsSpawn is the Windows launch primitive.");

        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using WindowsSpawnedProcess child = WindowsSpawn.SpawnSuspendedRedirected(
            cmd, new[] { "/d", "/c", "echo", "DRHOOK_SPAWN_%DRHOOK_SPAWN_VAR%" }, workingDirectory: null,
            new Dictionary<string, string> { ["DRHOOK_SPAWN_VAR"] = "held-and-redirected" });
        using var stdout = new FileStream(child.StdoutRead, FileAccess.Read, bufferSize: 0);
        using var stderr = new FileStream(child.StderrRead, FileAccess.Read, bufferSize: 0);
        using Process process = Process.GetProcessById(child.ProcessId);
        try
        {
            ProcessThread mainThread = process.Threads.Cast<ProcessThread>().Single();
            Assert.Equal(System.Diagnostics.ThreadState.Wait, mainThread.ThreadState);
            Assert.Equal(ThreadWaitReason.Suspended, mainThread.WaitReason);

            WindowsSpawn.Resume(child);

            string output = new StreamReader(stdout).ReadToEnd();
            Assert.True(process.WaitForExit(10_000), "cmd did not exit after resume.");
            Assert.Equal("DRHOOK_SPAWN_held-and-redirected", output.Trim());
        }
        finally
        {
            if (!process.HasExited) WindowsSpawn.Terminate(child);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
