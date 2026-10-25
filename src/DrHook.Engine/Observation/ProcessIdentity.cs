// Per-process identity for the process listings (finding 93). One read, shared by
// ProcessInspector.ListDotnetProcesses and the drhook_processes tool, so both handle a process the same way.
//
// A process whose executable was deleted or replaced while it ran (a rebuild does this) stays listed: it is
// typically an orphaned target running an old build, which is exactly what an operator looks for. On Linux,
// /proc/<pid>/exe then reads "<path> (deleted)" and FileVersionInfo throws FileNotFoundException on it; this
// reader strips the suffix, sets ExecutableDeleted and leaves FileVersion null instead of throwing.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SkyOmega.DrHook.Engine.Observation;

internal sealed record ProcessIdentity(
    int Pid,
    string Name,
    string? ExecutablePath,
    string? FileVersion,
    bool ExecutableDeleted)
{
    /// <summary>What Linux appends to <c>/proc/&lt;pid&gt;/exe</c> once the file has been unlinked.</summary>
    internal const string LinuxDeletedSuffix = " (deleted)";

    /// <summary>Read a process's identity. Returns null when the process has exited (or never existed).
    /// An executable that is gone or unreadable does not make it null: the process is returned with
    /// <see cref="ExecutableDeleted"/> set or a null path or version.</summary>
    internal static ProcessIdentity? TryRead(int pid)
    {
        Process proc;
        try { proc = Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; }   // not running

        using (proc)
        {
            string name;
            try { name = proc.ProcessName; }
            catch (InvalidOperationException) { return null; }   // exited between discovery and here

            string? rawPath = null;
            try { rawPath = proc.MainModule?.FileName; }
            catch (InvalidOperationException) { return null; }   // exited
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException)
            {
                // The main module can't be read (e.g. access denied): still list the process, without a path.
            }

            (string? path, bool deleted) = ParseExecutablePath(rawPath, OperatingSystem.IsLinux());
            if (path is not null && !deleted && !File.Exists(path))
                deleted = true;   // macOS and Windows have no suffix; a missing file means the same thing

            string? version = null;
            if (path is not null && !deleted)
            {
                try { version = FileVersionInfo.GetVersionInfo(path).FileVersion; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    deleted = !File.Exists(path);   // deleted between the check and the read
                }
            }

            return new ProcessIdentity(pid, name, path, version, deleted);
        }
    }

    /// <summary>Split the raw main-module path into the executable's path and whether it was deleted. Only
    /// Linux marks deletion in the path itself, so the suffix is only stripped there.</summary>
    internal static (string? Path, bool Deleted) ParseExecutablePath(string? rawPath, bool isLinux)
    {
        if (rawPath is null) return (null, false);
        if (isLinux && rawPath.EndsWith(LinuxDeletedSuffix, StringComparison.Ordinal))
            return (rawPath[..^LinuxDeletedSuffix.Length], true);
        return (rawPath, false);
    }
}
