// SparseFile.cs
// Sets a file's length without reserving disk for the part that has not been written.
// No external dependencies, only BCL + P/Invoke.
// .NET 10 / C# 14

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SkyOmega.Mercury.Runtime;

/// <summary>
/// Sets the length of a memory-mapped storage file so that disk is used only where data is written.
/// </summary>
/// <remarks>
/// <para>
/// Mercury sizes its mapped files ahead of use: a bulk-mode Reference index starts at 1 TB, a
/// Temporal index at 256 GB, and even a default store presizes about 5.6 GB across its files. That is
/// only affordable if the unwritten length costs nothing on disk.
/// </para>
/// <para>
/// On macOS (APFS) and Linux (ext4, XFS, Btrfs, tmpfs) extending a file with <c>ftruncate</c> makes
/// it sparse by itself. NTFS does not: <see cref="FileStream.SetLength"/> reserves the whole length
/// unless the file is first marked sparse (<c>FSCTL_SET_SPARSE</c>). Measured on omega-windows
/// (Windows 11 arm64, NTFS, 2026-10-26): a plain 300 GB file failed with "There is not enough space on
/// the disk" and a plain 8 GB file took 8 GB of free space; marked sparse, files of 300 GB and 1 TB took
/// 0 bytes, and a full-length mapped view with writes at both ends worked.
/// </para>
/// <para>
/// The sparse attribute is stored with the file, so later growth of the same file stays sparse, and
/// a file created by an earlier Mercury without the attribute becomes sparse from its next growth on.
/// A filesystem without sparse support (FAT32, exFAT, some network shares) refuses the attribute; the
/// length is then set anyway and is fully reserved, so a shortage surfaces as the filesystem's own
/// disk-full <see cref="IOException"/>, the true state of that disk.
/// </para>
/// </remarks>
public static class SparseFile
{
    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    /// <summary>
    /// Sets <paramref name="file"/>'s length to <paramref name="length"/>; on Windows the file is
    /// marked sparse first, so the extension reserves no disk.
    /// </summary>
    /// <param name="file">An open, writable file.</param>
    /// <param name="length">The new length in bytes.</param>
    public static void SetLength(FileStream file, long length)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (OperatingSystem.IsWindows() && length > file.Length)
            _ = DeviceIoControl(file.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        file.SetLength(length);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}
