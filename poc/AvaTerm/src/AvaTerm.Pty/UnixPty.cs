using System.Runtime.InteropServices;

namespace AvaTerm.Pty;

/// <summary>
/// Pseudo terminal for Linux and macOS built on posix_openpt + posix_spawn.
/// fork() is not safe inside a managed process, so the child is set up entirely through
/// spawn attributes: POSIX_SPAWN_SETSID makes it a session leader, and opening the slave
/// device as fd 0 then gives it that device as controlling terminal.
/// </summary>
internal sealed unsafe class UnixPty : PtyConnection
{
    // Every spawn must close the masters of all other terminals in the child,
    // otherwise a shell in one tab would keep another tab's terminal alive.
    static readonly object SpawnLock = new();
    static readonly HashSet<int> OpenMasters = [];

    const int SIGHUP = 1, SIGKILL = 9, EINTR = 4;
    static readonly bool Mac = OperatingSystem.IsMacOS();

    int _master = -1;
    readonly object _writeLock = new();
    readonly int _pid;
    volatile bool _exited;

    public UnixPty(PtyOptions o)
    {
        string command = o.Command ?? PtyFactory.DefaultShell();
        string path = PtyFactory.FindOnPath(command) ?? command;

        var argv = new List<string>();
        // On macOS a GUI app has a bare environment; a login shell gives the user their PATH.
        argv.Add(o.Command == null && Mac ? "-" + Path.GetFileName(path) : command);
        argv.AddRange(o.Args);

        var env = PtyFactory.BuildEnvironment(o with { Environment = new Dictionary<string, string?>() }, false);
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "AvaTerm";
        env.Remove("TMUX");
        env.Remove("TMUX_PANE");
        if (!env.ContainsKey("LANG") && !env.ContainsKey("LC_ALL") && !env.ContainsKey("LC_CTYPE"))
            env["LANG"] = Mac ? "en_US.UTF-8" : "C.UTF-8";
        foreach (var (k, v) in o.Environment)
        {
            if (v == null) env.Remove(k);
            else env[k] = v;
        }

        string cwd = o.WorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        lock (SpawnLock)
        {
            // Linux also takes O_CLOEXEC here, keeping the master out of unrelated child processes.
            int flags = 2 /* O_RDWR */ | (Mac ? 0x20000 : 0x100 | 0x80000) /* O_NOCTTY [| O_CLOEXEC] */;
            _master = Libc.posix_openpt(flags);
            if (_master < 0) throw Fail("posix_openpt");
            try
            {
                if (Libc.grantpt(_master) != 0) throw Fail("grantpt");
                if (Libc.unlockpt(_master) != 0) throw Fail("unlockpt");
                string slave = Marshal.PtrToStringUTF8(Libc.ptsname(_master)) ?? throw Fail("ptsname");
                EnableUtf8(_master);
                SetWindowSize(_master, o.Cols, o.Rows);
                _pid = Spawn(path, argv, env, slave, cwd);
            }
            catch
            {
                Libc.close(_master);
                _master = -1;
                throw;
            }
            OpenMasters.Add(_master);
        }

        ProcessId = _pid;
        StartWriter();
        new Thread(WaitLoop) { IsBackground = true, Name = "pty-wait" }.Start();
    }

    static Exception Fail(string what) =>
        new IOException($"{what} failed (errno {Marshal.GetLastPInvokeError()}).");

    int Spawn(string path, List<string> argv, Dictionary<string, string> env, string slave, string cwd)
    {
        var allocations = new List<IntPtr>();
        IntPtr Alloc(int size)
        {
            IntPtr p = Marshal.AllocCoTaskMem(size);
            new Span<byte>((void*)p, size).Clear();
            allocations.Add(p);
            return p;
        }
        IntPtr StringArray(IEnumerable<string> items)
        {
            var list = items.ToList();
            IntPtr array = Alloc((list.Count + 1) * IntPtr.Size);
            for (int i = 0; i < list.Count; i++)
            {
                IntPtr s = Marshal.StringToCoTaskMemUTF8(list[i]);
                allocations.Add(s);
                Marshal.WriteIntPtr(array, i * IntPtr.Size, s);
            }
            return array;
        }

        // posix_spawn_file_actions_t / posix_spawnattr_t / sigset_t are opaque and differ in
        // size per platform (pointer-sized on macOS, up to 336 bytes on glibc). Over-allocate.
        IntPtr actions = Alloc(1024), attr = Alloc(1024), sigDefault = Alloc(256), sigMask = Alloc(256);
        bool actionsInit = false, attrInit = false;
        try
        {
            Check(Libc.posix_spawn_file_actions_init(actions), "file_actions_init");
            actionsInit = true;
            Check(Libc.posix_spawnattr_init(attr), "spawnattr_init");
            attrInit = true;

            foreach (int fd in OpenMasters) Libc.posix_spawn_file_actions_addclose(actions, fd);
            Libc.posix_spawn_file_actions_addclose(actions, _master);
            Check(Libc.posix_spawn_file_actions_addopen(actions, 0, slave, 2 /* O_RDWR */, 0), "addopen");
            Check(Libc.posix_spawn_file_actions_adddup2(actions, 0, 1), "adddup2");
            Check(Libc.posix_spawn_file_actions_adddup2(actions, 0, 2), "adddup2");
            if (Directory.Exists(cwd))
            {
                try { Libc.posix_spawn_file_actions_addchdir_np(actions, cwd); }
                catch (EntryPointNotFoundException) { /* older libc: start in our own directory */ }
            }

            // The runtime ignores SIGPIPE and may block signals; give the child a clean slate.
            Libc.sigfillset(sigDefault);
            Libc.sigdelset(sigDefault, SIGKILL);
            Libc.sigdelset(sigDefault, Mac ? 17 : 19 /* SIGSTOP */);
            Libc.sigemptyset(sigMask);
            Check(Libc.posix_spawnattr_setsigdefault(attr, sigDefault), "setsigdefault");
            Check(Libc.posix_spawnattr_setsigmask(attr, sigMask), "setsigmask");

            const int SETSIGDEF = 0x04, SETSIGMASK = 0x08;
            int flags = SETSIGDEF | SETSIGMASK | (Mac
                ? 0x0400 /* SETSID */ | 0x4000 /* CLOEXEC_DEFAULT */
                : 0x80 /* SETSID */);
            Check(Libc.posix_spawnattr_setflags(attr, (short)flags), "setflags");

            IntPtr argvPtr = StringArray(argv);
            IntPtr envPtr = StringArray(env.Select(kv => kv.Key + "=" + kv.Value));
            int rc = Libc.posix_spawn(out int pid, path, actions, attr, argvPtr, envPtr);
            if (rc != 0) throw new IOException($"Could not start '{path}' (errno {rc}).");
            return pid;
        }
        finally
        {
            if (actionsInit) Libc.posix_spawn_file_actions_destroy(actions);
            if (attrInit) Libc.posix_spawnattr_destroy(attr);
            foreach (var p in allocations) Marshal.FreeCoTaskMem(p);
        }

        static void Check(int rc, string what)
        {
            if (rc != 0) throw new IOException($"posix_spawn setup failed in {what} (errno {rc}).");
        }
    }

    static void EnableUtf8(int fd)
    {
        // termios starts with c_iflag on every supported platform (4 bytes on Linux, 8 on macOS,
        // little-endian either way), so OR-ing IUTF8 into the first word works without the full layout.
        const int IUTF8 = 0x4000;
        byte* termios = stackalloc byte[256];
        new Span<byte>(termios, 256).Clear();
        if (Libc.tcgetattr(fd, (IntPtr)termios) != 0) return;
        *(uint*)termios |= IUTF8;
        Libc.tcsetattr(fd, 0 /* TCSANOW */, (IntPtr)termios);
    }

    static void SetWindowSize(int fd, int cols, int rows)
    {
        var ws = new Libc.WinSize { Rows = (ushort)Math.Clamp(rows, 1, 4096), Cols = (ushort)Math.Clamp(cols, 1, 4096) };
        if (!Mac) Libc.ioctl(fd, 0x5414 /* TIOCSWINSZ */, ref ws);
        else if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            // ioctl is variadic. Apple's arm64 ABI passes variadic arguments on the stack, which a
            // fixed P/Invoke signature would not do; six dummy register arguments push ours there.
            Libc.ioctl_darwin_arm64(fd, 0x80087467, 0, 0, 0, 0, 0, 0, ref ws);
        else Libc.ioctl(fd, 0x80087467, ref ws);
    }

    public override int Read(byte[] buffer)
    {
        while (true)
        {
            int fd = _master;
            if (fd < 0) return 0;
            nint n;
            fixed (byte* p = buffer) n = Libc.read(fd, p, buffer.Length);
            if (n > 0) return (int)n;
            if (n < 0 && Marshal.GetLastPInvokeError() == EINTR) continue;
            // 0, or EIO once the last process on the slave side is gone. The reader owns the
            // descriptor's lifetime: closing it from another thread while read() is blocked
            // would race with descriptor reuse.
            CloseMaster();
            return 0;
        }
    }

    protected override void WriteCore(byte[] data)
    {
        fixed (byte* p = data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                lock (_writeLock)
                {
                    int fd = _master;
                    if (fd < 0) return;
                    nint n = Libc.write(fd, p + offset, data.Length - offset);
                    if (n > 0) offset += (int)n;
                    else if (Marshal.GetLastPInvokeError() != EINTR) return;
                }
            }
        }
    }

    public override void Resize(int cols, int rows)
    {
        lock (SpawnLock)
        {
            if (_master >= 0) SetWindowSize(_master, cols, rows);
        }
    }

    void WaitLoop()
    {
        int status = 0;
        while (true)
        {
            int r = Libc.waitpid(_pid, out status, 0);
            if (r == _pid) break;
            if (r < 0 && Marshal.GetLastPInvokeError() == EINTR) continue;
            status = 0;
            break;
        }
        _exited = true;
        int code = (status & 0x7F) == 0 ? status >> 8 & 0xFF : 128 + (status & 0x7F);
        RaiseExited(code);
    }

    void CloseMaster()
    {
        lock (_writeLock)
        lock (SpawnLock)
        {
            if (_master < 0) return;
            OpenMasters.Remove(_master);
            Libc.close(_master);
            _master = -1;
        }
    }

    protected override void DisposeCore()
    {
        if (_exited) return;
        // The shell is a session leader, so its pid doubles as the process group id.
        Libc.kill(-_pid, SIGHUP);
        Libc.kill(_pid, SIGHUP);
        _ = Task.Delay(1500).ContinueWith(_ =>
        {
            if (!_exited) Libc.kill(_pid, SIGKILL);
        });
    }
}

internal static unsafe class Libc
{
    const string Name = "libc";

    static Libc()
    {
        // "libc" is not a loadable name everywhere (glibc ships libc.so.6 without a dev package).
        NativeLibrary.SetDllImportResolver(typeof(Libc).Assembly, (name, _, _) =>
        {
            if (name != Name) return IntPtr.Zero;
            string[] candidates = OperatingSystem.IsMacOS()
                ? ["/usr/lib/libSystem.B.dylib", "libSystem.dylib"]
                : ["libc.so.6", "libc.so", "libc"];
            foreach (var c in candidates)
                if (NativeLibrary.TryLoad(c, out IntPtr handle)) return handle;
            return IntPtr.Zero;
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort Rows, Cols, XPixel, YPixel;
    }

    [DllImport(Name, SetLastError = true)] public static extern int posix_openpt(int flags);
    [DllImport(Name, SetLastError = true)] public static extern int grantpt(int fd);
    [DllImport(Name, SetLastError = true)] public static extern int unlockpt(int fd);
    [DllImport(Name, SetLastError = true)] public static extern IntPtr ptsname(int fd);
    [DllImport(Name, SetLastError = true)] public static extern nint read(int fd, byte* buffer, nint count);
    [DllImport(Name, SetLastError = true)] public static extern nint write(int fd, byte* buffer, nint count);
    [DllImport(Name)] public static extern int close(int fd);
    [DllImport(Name)] public static extern int kill(int pid, int signal);
    [DllImport(Name, SetLastError = true)] public static extern int waitpid(int pid, out int status, int options);
    [DllImport(Name)] public static extern int tcgetattr(int fd, IntPtr termios);
    [DllImport(Name)] public static extern int tcsetattr(int fd, int actions, IntPtr termios);

    [DllImport(Name, EntryPoint = "ioctl")]
    public static extern int ioctl(int fd, nuint request, ref WinSize size);

    [DllImport(Name, EntryPoint = "ioctl")]
    public static extern int ioctl_darwin_arm64(int fd, nuint request,
        nint pad2, nint pad3, nint pad4, nint pad5, nint pad6, nint pad7, ref WinSize size);

    [DllImport(Name)] public static extern int sigemptyset(IntPtr set);
    [DllImport(Name)] public static extern int sigfillset(IntPtr set);
    [DllImport(Name)] public static extern int sigdelset(IntPtr set, int signal);

    [DllImport(Name)] public static extern int posix_spawn_file_actions_init(IntPtr actions);
    [DllImport(Name)] public static extern int posix_spawn_file_actions_destroy(IntPtr actions);
    [DllImport(Name)] public static extern int posix_spawn_file_actions_addclose(IntPtr actions, int fd);
    [DllImport(Name)] public static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);
    [DllImport(Name)]
    public static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
    [DllImport(Name)]
    public static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Name)] public static extern int posix_spawnattr_init(IntPtr attr);
    [DllImport(Name)] public static extern int posix_spawnattr_destroy(IntPtr attr);
    [DllImport(Name)] public static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
    [DllImport(Name)] public static extern int posix_spawnattr_setsigdefault(IntPtr attr, IntPtr set);
    [DllImport(Name)] public static extern int posix_spawnattr_setsigmask(IntPtr attr, IntPtr set);

    [DllImport(Name)]
    public static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr actions, IntPtr attr, IntPtr argv, IntPtr envp);
}
