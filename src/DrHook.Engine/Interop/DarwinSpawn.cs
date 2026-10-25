// macOS suspended spawn: posix_spawnp with POSIX_SPAWN_START_SUSPENDED (an Apple extension in
// <spawn.h>), released with SIGCONT. The child never runs a single instruction before Continue, so
// the runtime-startup callback cannot be missed.
//
// ABI basis (macOS SDK <spawn.h>, <sys/signal.h>): posix_spawnattr_t and posix_spawn_file_actions_t
// are both `typedef void *` — an opaque pointer the init call allocates behind. POSIX_SPAWN_START_SUSPENDED
// = 0x0080. SIGCONT = 19.

using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class DarwinSpawn
{
    private const short POSIX_SPAWN_START_SUSPENDED = 0x0080;
    private const int SIGCONT = 19;

    /// <summary>Darwin <c>posix_spawnattr_t</c>: <c>typedef void *posix_spawnattr_t;</c></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpawnAttr { public nint Opaque; }

    /// <summary>Darwin <c>posix_spawn_file_actions_t</c>: <c>typedef void *posix_spawn_file_actions_t;</c></summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpawnFileActions { public nint Opaque; }

    internal static int SpawnSuspendedRedirected(string file, string[] argv, string? cwd,
        IReadOnlyDictionary<string, string>? env, out int pid, out int stdoutReadFd, out int stderrReadFd)
    {
        SpawnAttr attr;
        SpawnFileActions fileActions;
        return PosixSpawn.SpawnRedirected(&attr, &fileActions, POSIX_SPAWN_START_SUSPENDED,
            file, argv, cwd, env, out pid, out stdoutReadFd, out stderrReadFd);
    }

    internal static void Continue(int pid) => PosixSpawn.kill(pid, SIGCONT);
}
