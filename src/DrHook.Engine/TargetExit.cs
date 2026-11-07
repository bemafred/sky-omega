using System.Globalization;

namespace SkyOmega.DrHook.Engine;

/// <summary>How a target process ended, as the operating system records it.</summary>
public enum TargetExitKind
{
    /// <summary>The process ended with an exit code (<see cref="TargetExit.Code"/>). On Windows every end is one,
    /// a crash or a <c>TerminateProcess</c> included.</summary>
    Exited,
    /// <summary>A signal ended the process (<see cref="TargetExit.Signal"/>). POSIX only.</summary>
    Killed,
    /// <summary>DrHook could not read how the process ended; <see cref="TargetExit.WhyUnknown"/> says why.</summary>
    Unknown,
}

/// <summary>How the target ended, read from the operating system's record of it: the exit code it ended with, or the
/// signal that ended it. Never inferred from the debugger: an <c>ExitProcess</c> callback says only that the target is
/// gone (before 2026-11-07 DrHook reported every exit as "ran to completion").</summary>
public sealed record TargetExit(TargetExitKind Kind, int? Code = null, int? Signal = null, bool CoreDumped = false, string? WhyUnknown = null)
{
    public static TargetExit Exited(int code) => new(TargetExitKind.Exited, Code: code);
    public static TargetExit Killed(int signal, bool coreDumped) => new(TargetExitKind.Killed, Signal: signal, CoreDumped: coreDumped);
    public static TargetExit Unknown(string why) => new(TargetExitKind.Unknown, WhyUnknown: why);

    /// <summary>The signal's name, for the signals whose numbers are the same on Linux and macOS; null otherwise.</summary>
    public string? SignalName => Signal switch
    {
        1 => "SIGHUP", 2 => "SIGINT", 3 => "SIGQUIT", 4 => "SIGILL", 5 => "SIGTRAP", 6 => "SIGABRT", 8 => "SIGFPE",
        9 => "SIGKILL", 11 => "SIGSEGV", 13 => "SIGPIPE", 14 => "SIGALRM", 15 => "SIGTERM", _ => null,
    };

    /// <summary>The end as a predicate, to follow "The debuggee": "exited with code 3", "was killed by signal 9
    /// (SIGKILL)", or "exited, but how is unknown: …". A code outside 0–255 (a Windows NTSTATUS or HRESULT) is also
    /// given in hex.</summary>
    public string Description => Kind switch
    {
        TargetExitKind.Exited when Code is < 0 or > 255 =>
            $"exited with code {Code.Value.ToString(CultureInfo.InvariantCulture)} (0x{Code.Value:X8})",
        TargetExitKind.Exited => $"exited with code {Code!.Value.ToString(CultureInfo.InvariantCulture)}",
        TargetExitKind.Killed =>
            $"was killed by signal {Signal!.Value.ToString(CultureInfo.InvariantCulture)}" +
            (SignalName is { } name ? $" ({name})" : "") + (CoreDumped ? ", core dumped" : ""),
        _ => $"exited, but how is unknown: {WhyUnknown}",
    };
}
