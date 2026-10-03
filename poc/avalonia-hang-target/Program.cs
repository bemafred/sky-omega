using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace HangTarget;

// Positive control for the capture-liveness guard. The UI thread polls for a trigger file once per beat and,
// when it appears, hangs itself in the requested way and never returns to the run loop:
//   <trigger> contains "managed" — Monitor.Enter on a lock another thread holds forever (managed frames on top)
//   <trigger> contains "native"  — pthread_mutex_lock on a mutex another thread holds forever (native frame on top)
// Trigger path: $TMPDIR/drhook-hang-<pid>. Absent trigger = a normal idle GUI app (the negative control).

internal sealed class App : Application
{
    private static readonly object HeldForever = new();
    private static int _beat;

    public App() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window { Title = "HangTarget", Width = 320, Height = 200, Content = new TextBlock { Text = "alive" } };
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) => Beat();
            timer.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void Beat()
    {
        _beat++;
        string trigger = Path.Combine(Path.GetTempPath(), $"drhook-hang-{Environment.ProcessId}");
        if (!File.Exists(trigger)) return;
        string mode = File.ReadAllText(trigger).Trim();
        File.Delete(trigger);
        if (mode == "managed") HangManaged();
        if (mode == "native") HangNative();
    }

    private static void HangManaged()
    {
        var held = new ManualResetEventSlim();
        new Thread(() => { Monitor.Enter(HeldForever); held.Set(); Thread.Sleep(Timeout.Infinite); }) { IsBackground = true }.Start();
        held.Wait();
        Monitor.Enter(HeldForever); // never returns
    }

    private static unsafe void HangNative()
    {
        nint mutex = Marshal.AllocHGlobal(64);
        pthread_mutex_init(mutex, 0);
        var held = new ManualResetEventSlim();
        new Thread(() => { pthread_mutex_lock(mutex); held.Set(); Thread.Sleep(Timeout.Infinite); }) { IsBackground = true }.Start();
        held.Wait();
        pthread_mutex_lock(mutex); // never returns
    }

    [DllImport("libc")] private static extern int pthread_mutex_init(nint mutex, nint attr);
    [DllImport("libc")] private static extern int pthread_mutex_lock(nint mutex);
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
