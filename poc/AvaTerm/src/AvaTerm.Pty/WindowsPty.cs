using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AvaTerm.Pty;

/// <summary>Pseudo terminal for Windows 10 1809+ built on ConPTY.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPty : PtyConnection
{
    readonly object _lock = new();
    IntPtr _console;
    readonly IntPtr _process;
    readonly FileStream _input, _output;

    public WindowsPty(PtyOptions o)
    {
        string command = o.Command ?? PtyFactory.DefaultShell();
        string exe = PtyFactory.FindOnPath(command) ?? PtyFactory.FindOnPath(command + ".exe") ?? command;

        var commandLine = new StringBuilder(Quote(exe));
        foreach (var arg in o.Args) commandLine.Append(' ').Append(Quote(arg));

        if (!Native.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw Fail("CreatePipe");
        if (!Native.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) throw Fail("CreatePipe");

        int hr = Native.CreatePseudoConsole(Size(o.Cols, o.Rows), inRead, outWrite, 0, out _console);
        // ConPTY duplicates its ends of the pipes; ours are no longer needed.
        inRead.Dispose();
        outWrite.Dispose();
        if (hr != 0)
        {
            inWrite.Dispose();
            outRead.Dispose();
            throw new IOException($"CreatePseudoConsole failed (HRESULT 0x{hr:X8}). ConPTY needs Windows 10 1809 or later.");
        }

        IntPtr attrList = IntPtr.Zero, cmdPtr = IntPtr.Zero, envPtr = IntPtr.Zero;
        try
        {
            IntPtr size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw Fail("InitializeProcThreadAttributeList");
            if (!Native.UpdateProcThreadAttribute(attrList, 0, (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Fail("UpdateProcThreadAttribute");

            var si = new Native.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            // Without this flag a child can pick up the parent's redirected std handles instead of the pty.
            si.StartupInfo.dwFlags = Native.STARTF_USESTDHANDLES;
            si.lpAttributeList = attrList;

            uint flags = Native.EXTENDED_STARTUPINFO_PRESENT;
            if (o.Environment.Count > 0)
            {
                var env = PtyFactory.BuildEnvironment(o, true);
                var block = new StringBuilder();
                foreach (var kv in env.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                    block.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
                block.Append('\0');
                envPtr = Marshal.StringToHGlobalUni(block.ToString());
                flags |= Native.CREATE_UNICODE_ENVIRONMENT;
            }

            string cwd = o.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Directory.Exists(cwd)) cwd = Environment.CurrentDirectory;

            cmdPtr = Marshal.StringToHGlobalUni(commandLine.ToString());
            if (!Native.CreateProcessW(null, cmdPtr, IntPtr.Zero, IntPtr.Zero, false, flags, envPtr, cwd, ref si, out var pi))
                throw Fail($"CreateProcess '{exe}'");

            Native.CloseHandle(pi.hThread);
            _process = pi.hProcess;
            ProcessId = pi.dwProcessId;
        }
        catch
        {
            Native.ClosePseudoConsole(_console);
            _console = IntPtr.Zero;
            inWrite.Dispose();
            outRead.Dispose();
            throw;
        }
        finally
        {
            if (attrList != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            if (cmdPtr != IntPtr.Zero) Marshal.FreeHGlobal(cmdPtr);
            if (envPtr != IntPtr.Zero) Marshal.FreeHGlobal(envPtr);
        }

        _input = new FileStream(inWrite, FileAccess.Write, 1, false);
        _output = new FileStream(outRead, FileAccess.Read, 1, false);

        StartWriter();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty-wait" }.Start();
    }

    static Native.COORD Size(int cols, int rows) =>
        new() { X = (short)Math.Clamp(cols, 1, short.MaxValue), Y = (short)Math.Clamp(rows, 1, short.MaxValue) };

    static Exception Fail(string what) =>
        new IOException($"{what} failed (Win32 error {Marshal.GetLastPInvokeError()}).");

    /// <summary>Quotes one argument the way CommandLineToArgvW will undo.</summary>
    static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public override int Read(byte[] buffer)
    {
        try { return _output.Read(buffer, 0, buffer.Length); }
        catch (IOException) { return 0; }
        catch (ObjectDisposedException) { return 0; }
    }

    protected override void WriteCore(byte[] data)
    {
        _input.Write(data, 0, data.Length);
        _input.Flush();
    }

    public override void Resize(int cols, int rows)
    {
        lock (_lock)
        {
            if (_console != IntPtr.Zero) Native.ResizePseudoConsole(_console, Size(cols, rows));
        }
    }

    void WaitLoop()
    {
        Native.WaitForSingleObject(_process, 0xFFFFFFFF);
        Native.GetExitCodeProcess(_process, out uint code);
        Native.CloseHandle(_process);
        // Closing the console ends the output pipe, which lets the reader finish.
        CloseConsole();
        RaiseExited(unchecked((int)code));
        try { _input.Dispose(); } catch (IOException) { }
    }

    void CloseConsole()
    {
        IntPtr console;
        lock (_lock)
        {
            console = _console;
            _console = IntPtr.Zero;
        }
        if (console != IntPtr.Zero) Native.ClosePseudoConsole(console);
    }

    // ClosePseudoConsole can wait for the client to exit, so keep it off the caller's thread.
    protected override void DisposeCore() => Task.Run(CloseConsole);

    static class Native
    {
        public const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const int STARTF_USESTDHANDLES = 0x00000100;
        public const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        [StructLayout(LayoutKind.Sequential)]
        public struct COORD { public short X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        const string K32 = "kernel32.dll";

        [DllImport(K32, SetLastError = true)]
        public static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

        [DllImport(K32)]
        public static extern int CreatePseudoConsole(COORD size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

        [DllImport(K32)]
        public static extern int ResizePseudoConsole(IntPtr console, COORD size);

        [DllImport(K32)]
        public static extern void ClosePseudoConsole(IntPtr console);

        [DllImport(K32, SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport(K32, SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value,
            IntPtr size, IntPtr previous, IntPtr returnSize);

        [DllImport(K32)]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport(K32, SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string? application, IntPtr commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory,
            ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);

        [DllImport(K32)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport(K32)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport(K32)]
        public static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);
    }
}
