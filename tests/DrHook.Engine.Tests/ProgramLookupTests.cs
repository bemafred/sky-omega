// Which file a POSIX launch runs (ProgramLookup, before DbgShim.LaunchWithDebuggerPosix spawns anything).
//
// Before 2026-11-07 the lookup was the spawn's: posix_spawnp's on macOS, and on Linux the shell's, because the Linux
// spawn is /bin/sh, which holds itself and then execs the program (LinuxSpawn). A missing program therefore failed on
// Linux after the shell had started, read as the shell's exit 127 and its "not found", not as the program's error.
// DrHook now finds the program itself, on both platforms, and spawns the path it found: a path with a slash, which
// neither posix_spawn nor the shell looks up again.

using System.Runtime.Versioning;
using SkyOmega.DrHook.Engine.Interop;
using Xunit;

namespace SkyOmega.DrHook.Engine.Tests;

[UnsupportedOSPlatform("windows")]   // file modes are POSIX; each test also skips on Windows at run time
public sealed class ProgramLookupTests
{
    [Fact]
    public void APathThatDoesNotExist_IsReportedWithTheOperatingSystemsError()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX lookup; Windows launches through CreateProcess's own search.");
        string missing = Path.Combine(Path.GetTempPath(), $"drhook-lookup-{Guid.NewGuid():N}", "program");

        bool found = ProgramLookup.TryFind(missing, cwd: null, pathVariable: "/usr/bin:/bin", out string path, out string whyNot);

        Assert.False(found);
        Assert.Equal($"{missing}: errno 2 (No such file or directory)", whyNot);
        Assert.Equal("", path);
    }

    [Fact]
    public void APathToAFileThatIsNotExecutable_IsReportedAsPermissionDenied()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX lookup; Windows launches through CreateProcess's own search.");
        DirectoryInfo dir = Directory.CreateTempSubdirectory("drhook-lookup-");
        try
        {
            string program = Path.Combine(dir.FullName, "program");
            File.WriteAllText(program, "#!/bin/sh\n");
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            bool found = ProgramLookup.TryFind(program, cwd: null, pathVariable: "/usr/bin:/bin", out _, out string whyNot);

            Assert.False(found);
            Assert.Equal($"{program}: errno 13 (Permission denied)", whyNot);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void APathToADirectory_IsReportedAsADirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX lookup; Windows launches through CreateProcess's own search.");
        DirectoryInfo dir = Directory.CreateTempSubdirectory("drhook-lookup-");
        try
        {
            bool found = ProgramLookup.TryFind(dir.FullName, cwd: null, pathVariable: "/usr/bin:/bin", out _, out string whyNot);

            Assert.False(found);
            Assert.Equal($"{dir.FullName}: is a directory", whyNot);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ARelativePath_IsCheckedInTheWorkingDirectory_AndSpawnedAsGiven()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX lookup; Windows launches through CreateProcess's own search.");
        DirectoryInfo dir = Directory.CreateTempSubdirectory("drhook-lookup-");
        try
        {
            string program = Path.Combine(dir.FullName, "program");
            File.WriteAllText(program, "#!/bin/sh\n");
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            bool found = ProgramLookup.TryFind("./program", cwd: dir.FullName, pathVariable: "/usr/bin:/bin", out string path, out string whyNot);

            Assert.True(found, whyNot);
            Assert.Equal("./program", path);   // the child changes to the working directory before it runs the path
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ABareName_IsFoundInTheFirstDirectoryOnPathWhereItCanRun()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX lookup; Windows launches through CreateProcess's own search.");
        DirectoryInfo root = Directory.CreateTempSubdirectory("drhook-lookup-");
        try
        {
            string empty = Directory.CreateDirectory(Path.Combine(root.FullName, "empty")).FullName;
            string notExecutable = Directory.CreateDirectory(Path.Combine(root.FullName, "not-executable")).FullName;
            File.WriteAllText(Path.Combine(notExecutable, "program"), "#!/bin/sh\n");
            File.SetUnixFileMode(Path.Combine(notExecutable, "program"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string directory = Directory.CreateDirectory(Path.Combine(root.FullName, "directory")).FullName;
            Directory.CreateDirectory(Path.Combine(directory, "program"));
            string runnable = Directory.CreateDirectory(Path.Combine(root.FullName, "runnable")).FullName;
            File.WriteAllText(Path.Combine(runnable, "program"), "#!/bin/sh\n");
            File.SetUnixFileMode(Path.Combine(runnable, "program"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            bool found = ProgramLookup.TryFind("program", cwd: null, pathVariable: $"{empty}:{notExecutable}:{directory}:{runnable}",
                out string path, out string whyNot);

            Assert.True(found, whyNot);
            Assert.Equal(Path.Combine(runnable, "program"), path);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void ABareNameInNoDirectoryOnPath_IsReportedAsNotFound()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX lookup; Windows launches through CreateProcess's own search.");
        DirectoryInfo dir = Directory.CreateTempSubdirectory("drhook-lookup-");
        try
        {
            bool found = ProgramLookup.TryFind("program", cwd: null, pathVariable: dir.FullName, out _, out string whyNot);

            Assert.False(found);
            Assert.Equal("program: not found in any directory on PATH", whyNot);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ABareNameOnPathOnlyWhereItCannotRun_IsReportedWithTheFirstReason()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX lookup; Windows launches through CreateProcess's own search.");
        DirectoryInfo root = Directory.CreateTempSubdirectory("drhook-lookup-");
        try
        {
            string notExecutable = Directory.CreateDirectory(Path.Combine(root.FullName, "not-executable")).FullName;
            File.WriteAllText(Path.Combine(notExecutable, "program"), "#!/bin/sh\n");
            File.SetUnixFileMode(Path.Combine(notExecutable, "program"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string directory = Directory.CreateDirectory(Path.Combine(root.FullName, "directory")).FullName;
            Directory.CreateDirectory(Path.Combine(directory, "program"));

            bool found = ProgramLookup.TryFind("program", cwd: null, pathVariable: $"{notExecutable}:{directory}", out _, out string whyNot);

            Assert.False(found);
            Assert.Equal($"program is on PATH only where it cannot run: {Path.Combine(notExecutable, "program")}: errno 13 (Permission denied)", whyNot);
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public void AnEmptyEntryOnPath_IsTheWorkingDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX lookup; Windows launches through CreateProcess's own search.");
        DirectoryInfo dir = Directory.CreateTempSubdirectory("drhook-lookup-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "program"), "#!/bin/sh\n");
            File.SetUnixFileMode(Path.Combine(dir.FullName, "program"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            bool found = ProgramLookup.TryFind("program", cwd: dir.FullName, pathVariable: "/nonexistent-drhook-dir:", out string path, out string whyNot);

            Assert.True(found, whyNot);
            Assert.Equal("./program", path);   // with a slash, so neither posix_spawn nor the shell looks it up again
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ABareNameWithoutPath_IsReportedAsUnresolvable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX lookup; Windows launches through CreateProcess's own search.");

        bool found = ProgramLookup.TryFind("dotnet", cwd: null, pathVariable: null, out _, out string whyNot);

        Assert.False(found);
        Assert.Equal("dotnet: PATH is not set, so it cannot be looked up; give its path", whyNot);
    }
}
