namespace SkyOmega.DrHook.Engine;

/// <summary>Which target a <c>drhook_kill</c> without an active session ends: one that <c>drhook_stop</c> left running
/// (ADR-016 6.2), named by its PID when there is more than one. Exactly one of <see cref="Pid"/> and
/// <see cref="Refusal"/> is set. drhook-mcp chooses this way among the workers that hold such targets, and a session
/// among its own (ADR-016 6.7), so both refuse with the same words.</summary>
public readonly record struct LeftRunningChoice(int? Pid, string? Refusal)
{
    /// <summary>Choose among the PIDs of the targets still running: the one <paramref name="wanted"/> names, or the only
    /// one when none is named.</summary>
    public static LeftRunningChoice Among(IReadOnlyCollection<int> running, int? wanted)
    {
        List<int> candidates = wanted is int pid ? running.Where(p => p == pid).ToList() : running.ToList();
        if (candidates.Count == 1) return new LeftRunningChoice(candidates[0], null);
        if (candidates.Count == 0)
            return new LeftRunningChoice(null, wanted is int p
                ? $"No active stepping session, and no running target {p} that drhook_stop left running."
                : "No active stepping session, and no target that drhook_stop left running.");
        return new LeftRunningChoice(null, "No active stepping session, and drhook_stop left more than one target running: " +
                                           $"pass the pid of the one to end ({string.Join(", ", candidates)}).");
    }
}
