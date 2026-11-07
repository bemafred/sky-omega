// Which file a POSIX launch runs: DrHook finds the program itself, before anything is spawned, and spawns the path it
// found. That path always holds a slash, so neither posix_spawn (macOS) nor the shell that holds the child on Linux
// (LinuxSpawn) looks it up again: there is one lookup, this one, on both platforms. A program that cannot be run is
// reported here with the operating system's error for it. Until 2026-11-07 the lookup was the spawn's, and a missing
// program on Linux read as the holding shell's exit 127 and its "not found", after the shell had started.
//
// The rules are execvp's (POSIX): a name with a slash is a path, used as given and relative to the child's working
// directory; a name without one is looked for in each directory on PATH in turn, an empty entry meaning the working
// directory. PATH is DrHook's own, as posix_spawnp's search on both platforms is the caller's, not the child's.
// A candidate that is missing (ENOENT) or under a non-directory (ENOTDIR) is passed over silently; one that exists but
// cannot run (a directory, no permission, a symlink loop) is passed over too, and the first such reason is the one
// reported if nothing runnable is found. glibc's execvp stops at an error other than ENOENT, ENOTDIR and EACCES, and
// BSD's goes on; this goes on, the same on both platforms.
//
// What runs is checked with access(X_OK), the operating system's answer for that file. It uses the real user id where
// exec uses the effective one, which are the same for DrHook (it is not setuid). A check cannot see everything exec
// will: a file that is present and executable can still fail to start (its ELF interpreter missing, another
// architecture's binary, a filesystem mounted noexec). Those still fail after the spawn, as they did before.

using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal static class ProgramLookup
{
    private const int X_OK = 1;                                // <unistd.h>, same on Darwin and Linux
    private const int ENOENT = 2;                              // <errno.h>, same on Darwin and Linux
    private const int ENOTDIR = 20;                            // <errno.h>, same on Darwin and Linux

    [DllImport("libc", SetLastError = true)]
    private static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

    /// <summary>Find the file a launch of <paramref name="program"/> runs. True with <paramref name="path"/>, the path to
    /// spawn (it holds a slash; relative paths are relative to <paramref name="cwd"/>, which the child changes to before
    /// it runs it). False with <paramref name="whyNot"/>, the operating system's error for the program, when there is no
    /// file it can run. <paramref name="pathVariable"/> is the PATH to search for a name without a slash.</summary>
    internal static bool TryFind(string program, string? cwd, string? pathVariable, out string path, out string whyNot)
    {
        path = "";
        whyNot = "";
        if (program.Length == 0)
        {
            whyNot = "the program name is empty";
            return false;
        }

        if (program.Contains('/'))
        {
            if (CanRun(program, cwd, out whyNot, out _))
            {
                path = program;
                return true;
            }
            return false;
        }

        if (string.IsNullOrEmpty(pathVariable))
        {
            whyNot = $"{program}: PATH is not set, so it cannot be looked up; give its path";
            return false;
        }

        string firstReason = "";
        foreach (string entry in pathVariable.Split(':'))
        {
            string candidate = Path.Join(entry.Length == 0 ? "." : entry, program);
            if (CanRun(candidate, cwd, out string reason, out int errno))
            {
                path = candidate;
                return true;
            }
            if (errno is ENOENT or ENOTDIR) continue;            // not in this directory
            if (firstReason.Length == 0) firstReason = reason;
        }
        whyNot = firstReason.Length > 0
            ? $"{program} is on PATH only where it cannot run: {firstReason}"
            : $"{program}: not found in any directory on PATH";
        return false;
    }

    /// <summary>Whether <paramref name="candidate"/> is a file this process can run. When not, <paramref name="reason"/>
    /// says why, as "path: reason", and <paramref name="errno"/> is access's error (0 for a directory, which access
    /// passes and exec refuses).</summary>
    private static bool CanRun(string candidate, string? cwd, out string reason, out int errno)
    {
        // Checked where the child will look: relative to its working directory. Path.Join does not normalize, so
        // ".." and symlinks resolve as exec resolves them.
        string checkedPath = cwd is not null && !Path.IsPathRooted(candidate) ? Path.Join(cwd, candidate) : candidate;
        reason = "";
        errno = 0;
        if (Directory.Exists(checkedPath))
        {
            reason = $"{checkedPath}: is a directory";
            return false;
        }
        if (access(checkedPath, X_OK) == 0) return true;
        errno = Marshal.GetLastPInvokeError();
        reason = $"{checkedPath}: errno {errno} ({Marshal.GetPInvokeErrorMessage(errno)})";
        return false;
    }
}
