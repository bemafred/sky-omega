// macOS libproc — proc_pidinfo, the kernel's per-process introspection call. Same-user targets need no task
// port, so it works where task_for_pid-based tools need the debugger entitlement or a Developer Tools prompt.
// Consumers: ProcessParentage (PROC_PIDTBSDINFO — finding 87) and MappedFiles (PROC_PIDREGIONPATHINFO — finding 88).

using System.Runtime.InteropServices;

namespace SkyOmega.DrHook.Engine.Interop;

internal static unsafe class LibProc
{
    [DllImport("libc", SetLastError = true, EntryPoint = "proc_pidinfo")]
    public static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int buffersize);
}
