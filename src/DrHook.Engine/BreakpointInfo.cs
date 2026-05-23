namespace SkyOmega.DrHook.Engine;

/// <summary>A handle to a breakpoint armed in a <see cref="DebugSession"/> — the engine assigns
/// monotonically increasing positive ids; <see cref="DebugSession.ListBreakpoints"/> returns these,
/// <see cref="DebugSession.RemoveBreakpoint"/> takes one. Subtyped by what the breakpoint binds to
/// (a source line vs. a function entry) rather than carrying optional fields, so MCP-layer list /
/// remove-by-key flows pattern-match on the concrete kind.</summary>
public abstract record BreakpointInfo(int Id);

/// <summary>A breakpoint armed at a source <c>(<see cref="FilePath"/>, <see cref="Line"/>)</c> via
/// <see cref="DebugSession.SetBreakpointAtLine"/>. <see cref="ModuleSubstring"/> is the substring the
/// caller used to disambiguate the target module.</summary>
public sealed record LineBreakpointInfo(int Id, string ModuleSubstring, string FilePath, int Line)
    : BreakpointInfo(Id);

/// <summary>A breakpoint armed at a method entry (<see cref="TypeName"/>.<see cref="MethodName"/>) via
/// <see cref="DebugSession.SetBreakpoint"/>. <see cref="ModuleSubstring"/> is the substring the
/// caller used to disambiguate the target module.</summary>
public sealed record FunctionBreakpointInfo(int Id, string ModuleSubstring, string TypeName, string MethodName)
    : BreakpointInfo(Id);
