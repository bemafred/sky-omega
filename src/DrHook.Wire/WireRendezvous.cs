namespace SkyOmega.DrHook.Wire;

/// <summary>The rendezvous — the well-known Unix-domain-socket path where the transport server listens and
/// views connect (ADR-012 Q7). It lives in the shared wire contract because BOTH ends need the same address:
/// the server (DrHook.Engine) binds it, and view clients (DrHook.Viz) connect to it, without either side
/// duplicating the convention. One active session at a time, so a fixed path is the simplest rendezvous.</summary>
public static class WireRendezvous
{
    /// <summary>The default per-host socket path: <c>~/Library/SkyOmega/drhook/session.sock</c> on macOS (the
    /// Sky Omega data-dir convention), an XDG-style path elsewhere. Windows AF_UNIX / multi-session keying are
    /// later refinements (the engine's POSIX-first pattern).</summary>
    public static string DefaultSocketPath()
    {
        string home = Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string dir = OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "SkyOmega", "drhook")
            : Path.Combine(home, ".local", "share", "sky-omega", "drhook");
        return Path.Combine(dir, "session.sock");
    }
}
