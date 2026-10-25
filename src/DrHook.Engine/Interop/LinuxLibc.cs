// Which C library a Linux host runs. It decides the native ABI DrHook may use (LinuxSpawn's struct
// layouts are glibc's) and the portable RID of the native assets it loads (linux-x64 vs
// linux-musl-x64). musl's dynamic loader is /lib/ld-musl-<arch>.so.1; glibc systems have none.

namespace SkyOmega.DrHook.Engine.Interop;

internal static class LinuxLibc
{
    /// <summary>True on a musl-libc Linux host (Alpine and similar). False on glibc and off Linux.</summary>
    public static bool IsMusl { get; } =
        OperatingSystem.IsLinux() && Directory.Exists("/lib") &&
        Directory.EnumerateFiles("/lib", "ld-musl-*").Any();
}
