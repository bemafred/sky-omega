// Windows launch spawn with ISOLATED stdio — the Windows counterpart of PosixSpawn (ADR-011 D2, probe 64).
//
// dbgshim's CreateProcessForLaunch takes no stdio handles. Probe 64 (omega-windows, 2026-10-26) showed what that
// means under drhook-mcp: the launched debuggee wrote to the server's stdout and stderr and READ the server's stdin,
// consuming the line an MCP client sent. So DrHook owns process creation on Windows too:
//
//   CreateProcessW(CREATE_SUSPENDED) with stdin = NUL and stdout/stderr = DrHook-owned pipes, passing ONLY those
//   three handles (PROC_THREAD_ATTRIBUTE_HANDLE_LIST) — no other inheritable handle of the host, such as the MCP
//   channel itself, reaches the child. The caller arms RegisterForRuntimeStartup, then calls Resume.
//
// CREATE_NO_WINDOW gives the child a console of its own with no window: console APIs work in the target, nothing
// it does reaches a console the host may share, and no window pops up when the host has no console.
//
// BCL + P/Invoke (kernel32) only.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SkyOmega.DrHook.Engine.Interop;

/// <summary>A suspended child created by <see cref="WindowsSpawn.SpawnSuspendedRedirected"/>. The caller owns every
/// handle: resume it with <see cref="WindowsSpawn.Resume"/>, read and dispose the two pipe read ends, and dispose
/// <see cref="Process"/> and <see cref="MainThread"/>.</summary>
internal sealed class WindowsSpawnedProcess : IDisposable
{
    public required int ProcessId { get; init; }
    public required SafeProcessHandle Process { get; init; }
    public required SafeFileHandle MainThread { get; init; }
    public required SafeFileHandle StdoutRead { get; init; }
    public required SafeFileHandle StderrRead { get; init; }

    /// <summary>Releases the process and thread handles (the pipe read ends belong to whoever drains them).</summary>
    public void Dispose()
    {
        MainThread.Dispose();
        Process.Dispose();
    }
}

internal static unsafe class WindowsSpawn
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private static readonly nint PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;

    /// <summary>Create <paramref name="program"/> SUSPENDED with stdin = NUL and stdout/stderr = fresh pipes whose
    /// read ends are returned. The environment is this process's, with <paramref name="env"/> set or replaced on
    /// top. Throws <see cref="Win32Exception"/> if Windows refuses any step; nothing is left behind on failure.</summary>
    public static WindowsSpawnedProcess SpawnSuspendedRedirected(string program, IReadOnlyList<string> args,
        string? workingDirectory, IReadOnlyDictionary<string, string>? env)
    {
        SECURITY_ATTRIBUTES inheritable = new() { nLength = sizeof(SECURITY_ATTRIBUTES), bInheritHandle = 1 };
        SafeFileHandle? stdoutRead = null, stdoutWrite = null, stderrRead = null, stderrWrite = null, nul = null;
        nint attributeList = 0;
        try
        {
            // Pipes: the write ends go to the child (inheritable), the read ends stay here (not inheritable).
            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref inheritable, 0)) throw Failure("CreatePipe(stdout)");
            if (!CreatePipe(out stderrRead, out stderrWrite, ref inheritable, 0)) throw Failure("CreatePipe(stderr)");
            if (!SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0)) throw Failure("SetHandleInformation(stdout read)");
            if (!SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0)) throw Failure("SetHandleInformation(stderr read)");

            // stdin = NUL: a launched debuggee must never read the host's stdin (the MCP request channel).
            nul = CreateFileW("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref inheritable, OPEN_EXISTING, 0, 0);
            if (nul.IsInvalid) throw Failure("CreateFile(NUL)");

            // Inherit exactly these three handles, not every inheritable handle the host happens to hold.
            nint listBytes = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref listBytes);
            attributeList = Marshal.AllocHGlobal(listBytes);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref listBytes)) throw Failure("InitializeProcThreadAttributeList");
            nint* inherited = stackalloc nint[3];
            inherited[0] = nul.DangerousGetHandle();
            inherited[1] = stdoutWrite.DangerousGetHandle();
            inherited[2] = stderrWrite.DangerousGetHandle();
            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inherited, 3 * sizeof(nint), 0, 0))
                throw Failure("UpdateProcThreadAttribute(HANDLE_LIST)");

            STARTUPINFOEXW startup = default;
            startup.StartupInfo.cb = sizeof(STARTUPINFOEXW);
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = inherited[0];
            startup.StartupInfo.hStdOutput = inherited[1];
            startup.StartupInfo.hStdError = inherited[2];
            startup.lpAttributeList = attributeList;

            // CreateProcessW may write into the command-line buffer, so it gets its own copy.
            char[] commandLine = (CommandLine(program, args) + "\0").ToCharArray();
            string environment = EnvironmentBlock(env);
            PROCESS_INFORMATION info;
            bool created;
            fixed (char* pCommandLine = commandLine)
            fixed (char* pEnvironment = environment)
            fixed (char* pCwd = workingDirectory)
            {
                created = CreateProcessW(null, pCommandLine, 0, 0, bInheritHandles: 1,
                    CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                    pEnvironment, pCwd, ref startup, out info);
            }
            if (!created) throw Failure($"CreateProcess({program})");

            return new WindowsSpawnedProcess
            {
                ProcessId = (int)info.dwProcessId,
                Process = new SafeProcessHandle(info.hProcess, ownsHandle: true),
                MainThread = new SafeFileHandle(info.hThread, ownsHandle: true),
                StdoutRead = stdoutRead,
                StderrRead = stderrRead,
            };
        }
        catch
        {
            stdoutRead?.Dispose();
            stderrRead?.Dispose();
            throw;
        }
        finally
        {
            // The child holds its own copies now; ours must close, or the read ends never see EOF when it exits.
            stdoutWrite?.Dispose();
            stderrWrite?.Dispose();
            nul?.Dispose();
            if (attributeList != 0)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
        }
    }

    /// <summary>Let a suspended child run.</summary>
    public static void Resume(WindowsSpawnedProcess child)
    {
        if (ResumeThread(child.MainThread) == uint.MaxValue) throw Failure("ResumeThread");
    }

    /// <summary>End a child that will not be handed over (a failed launch). Best effort.</summary>
    public static void Terminate(WindowsSpawnedProcess child) => _ = TerminateProcess(child.Process, 1);

    /// <summary>The command line for <paramref name="program"/> and <paramref name="args"/>, each quoted so the
    /// child's argument parser (CommandLineToArgvW rules) gets back exactly the strings given: backslashes are
    /// literal except before a double quote, where each one is doubled.</summary>
    internal static string CommandLine(string program, IReadOnlyList<string> args)
    {
        StringBuilder sb = new();
        AppendArgument(sb, program);
        foreach (string arg in args)
        {
            sb.Append(' ');
            AppendArgument(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (int i = 0; i < arg.Length; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }       // before the closing quote
            if (arg[i] == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
            else { sb.Append('\\', backslashes); sb.Append(arg[i]); }
        }
        sb.Append('"');
    }

    /// <summary>This process's environment with <paramref name="overrides"/> set or replaced on top, as a
    /// CREATE_UNICODE_ENVIRONMENT block: <c>name=value\0</c> entries sorted by name, ending in an extra
    /// <c>\0</c>. Windows names are case-insensitive, so an override replaces an entry of any case.</summary>
    internal static string EnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            map[(string)e.Key] = e.Value?.ToString() ?? string.Empty;
        if (overrides is not null)
        {
            foreach (KeyValuePair<string, string> kv in overrides)
            {
                map.Remove(kv.Key);
                map[kv.Key] = kv.Value;
            }
        }
        StringBuilder sb = new();
        foreach (KeyValuePair<string, string> kv in map)
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        return sb.ToString();
    }

    private static Win32Exception Failure(string step) => new(Marshal.GetLastWin32Error(), $"{step} failed.");

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public nint lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEXW
    {
        public STARTUPINFOW StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe, ref SECURITY_ATTRIBUTES attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES attributes, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint* value, nint size, nint previousValue, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        int bInheritHandles, uint creationFlags, char* environment, char* currentDirectory, ref STARTUPINFOEXW startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
