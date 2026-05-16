using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SkyOmega.Mercury.Runtime;

/// <summary>
/// ADR-040 / ADR-042 Part 5: host-physical-memory probe used by Mercury's substrate
/// to make adaptive sizing decisions at run start (readahead buffer sizing,
/// MPHF-construction memory budget, etc.). Returns an estimate of the bytes the
/// kernel could give the substrate right now — combining unused pages with
/// reclaimable inactive / purgeable pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Platform implementations:</b>
/// <list type="bullet">
///   <item><b>macOS</b>: <c>host_statistics64(HOST_VM_INFO64)</c> via <c>libSystem</c>.
///   Available ≈ <c>(free + inactive + purgeable + speculative) × page_size</c>.</item>
///   <item><b>Linux</b>: parses <c>/proc/meminfo</c>'s <c>MemAvailable</c> field
///   (the kernel-provided "estimated available" that accounts for reclaimable
///   slab + page cache).</item>
///   <item><b>Windows</b>: <c>GlobalMemoryStatusEx</c> from <c>kernel32.dll</c>;
///   reads <c>ullAvailPhys</c>.</item>
///   <item><b>Other / fallback</b>: <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>
///   — the BCL's best-effort estimate. On macOS this returns total RAM (not
///   what we want), hence the explicit Mach API path above.</item>
/// </list>
/// </para>
/// <para>
/// <b>Substrate-independence:</b> all paths are pure P/Invoke or BCL — no NuGet
/// dependency. The probe is called once per long-running operation (e.g., at
/// MergeAndWrite start), so per-call cost is irrelevant.
/// </para>
/// </remarks>
public static class ProcessMemoryProbe
{
    /// <summary>
    /// Returns the substrate's best estimate of the bytes the kernel could currently
    /// hand out to this process for new anonymous allocations. Conservative when the
    /// underlying OS facility is ambiguous.
    /// </summary>
    /// <returns>Available physical bytes, or a BCL fallback if the OS-specific path fails.</returns>
    public static long AvailablePhysicalBytes()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return MacOsAvailableBytes();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return LinuxAvailableBytes();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return WindowsAvailableBytes();
        }
        catch
        {
            // Any P/Invoke / parse failure falls through to the BCL fallback below.
        }
        return BclFallbackAvailableBytes();
    }

    // ===== BCL fallback =====
    // .NET's GC reports TotalAvailableMemoryBytes from the cgroup limit (Linux) or
    // GlobalMemoryStatusEx (Windows). On macOS it returns hw.memsize (total RAM,
    // not available) — the macOS-specific path above is required to avoid that gap.
    private static long BclFallbackAvailableBytes()
        => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    // ===== macOS — host_statistics64(HOST_VM_INFO64) =====
    //
    // mach/vm_statistics.h:
    //   struct vm_statistics64 {
    //       natural_t free_count, active_count, inactive_count, wire_count;
    //       uint64_t  zero_fill_count, reactivations, pageins, pageouts;
    //       uint64_t  faults, cow_faults, lookups, hits, purges;
    //       natural_t purgeable_count, speculative_count;
    //       uint64_t  decompressions, compressions, swapins, swapouts;
    //       natural_t compressor_page_count, throttled_count;
    //       natural_t external_page_count, internal_page_count;
    //       uint64_t  total_uncompressed_pages_in_compressor;
    //   };
    // natural_t = uint32, so struct layout is well-defined.

    private const int HOST_VM_INFO64 = 4;
    // Per Apple's <mach/host_info.h>: HOST_VM_INFO64_COUNT = sizeof(vm_statistics64_data_t) / sizeof(integer_t).
    // sizeof(vm_statistics64_data_t) on 64-bit aligned: 38 × 4-byte slots = 152, but the actual count
    // accepted by host_statistics64 is the maximum the kernel will fill; modern macOS uses 38.
    private const int HOST_VM_INFO64_COUNT = 38;

    [StructLayout(LayoutKind.Sequential)]
    private struct VmStatistics64
    {
        public uint free_count;
        public uint active_count;
        public uint inactive_count;
        public uint wire_count;
        public ulong zero_fill_count;
        public ulong reactivations;
        public ulong pageins;
        public ulong pageouts;
        public ulong faults;
        public ulong cow_faults;
        public ulong lookups;
        public ulong hits;
        public ulong purges;
        public uint purgeable_count;
        public uint speculative_count;
        public ulong decompressions;
        public ulong compressions;
        public ulong swapins;
        public ulong swapouts;
        public uint compressor_page_count;
        public uint throttled_count;
        public uint external_page_count;
        public uint internal_page_count;
        public ulong total_uncompressed_pages_in_compressor;
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint mach_host_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int host_statistics64(uint host_priv, int flavor, ref VmStatistics64 info, ref int info_count);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int sysctlbyname(string name, IntPtr oldp, ref UIntPtr oldlenp, IntPtr newp, UIntPtr newlen);

    private static long MacOsAvailableBytes()
    {
        long pageSize = GetMacOsPageSize();
        var info = default(VmStatistics64);
        int count = HOST_VM_INFO64_COUNT;
        int rc = host_statistics64(mach_host_self(), HOST_VM_INFO64, ref info, ref count);
        if (rc != 0)
            return BclFallbackAvailableBytes();
        // "Available" = pages the kernel could immediately reclaim or hand out:
        // free (untouched), inactive (LRU evictable), purgeable (kernel can drop),
        // speculative (read-ahead pages that can be dropped).
        long pages = (long)info.free_count + info.inactive_count + info.purgeable_count + info.speculative_count;
        return pages * pageSize;
    }

    private static long GetMacOsPageSize()
    {
        // sysctlbyname("hw.pagesize") — int64 on modern macOS. Use Marshal-allocated
        // native buffer to avoid the unsafe pinning idiom; cheap (one 8-byte alloc).
        IntPtr buf = Marshal.AllocHGlobal(8);
        try
        {
            var len = (UIntPtr)8;
            int rc = sysctlbyname("hw.pagesize", buf, ref len, IntPtr.Zero, UIntPtr.Zero);
            if (rc != 0)
                return 4096;
            long pageSize = Marshal.ReadInt64(buf);
            // ARM64 macOS uses 16384, Intel uses 4096. Some macOS releases return int32;
            // detect that via the upper-half-zero check and re-read as int32.
            if (pageSize <= 0 || (pageSize & ~0xFFFFFFFFL) != 0)
                pageSize = Marshal.ReadInt32(buf);
            return pageSize > 0 ? pageSize : 4096;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    // ===== Linux — /proc/meminfo's MemAvailable =====

    private static long LinuxAvailableBytes()
    {
        // /proc/meminfo line format:
        //   MemAvailable:    8123456 kB
        // The MemAvailable field is the kernel-provided estimate accounting for
        // reclaimable slab + page cache; introduced in Linux 3.14.
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                // Trim "MemAvailable:" prefix, strip whitespace and " kB" suffix.
                var span = line.AsSpan("MemAvailable:".Length).Trim();
                int spaceIdx = span.IndexOf(' ');
                if (spaceIdx > 0) span = span.Slice(0, spaceIdx);
                if (long.TryParse(span, out long kib))
                    return kib * 1024;
                break;
            }
        }
        return BclFallbackAvailableBytes();
    }

    // ===== Windows — GlobalMemoryStatusEx =====

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private static long WindowsAvailableBytes()
    {
        var ms = default(MEMORYSTATUSEX);
        ms.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        if (!GlobalMemoryStatusEx(ref ms))
            return BclFallbackAvailableBytes();
        return (long)ms.ullAvailPhys;
    }
}
