namespace SkyOmega.DrHook.Engine;

/// <summary>Substrate capability: resolve a property/field access on a local object reference at the
/// current stop by func-eval'ing the getter on the operand's runtime type. Named so substrate
/// consumers (expression walkers, custom evaluators) can declare their dependency minimally —
/// "I need member resolution," not "I need a full <see cref="DebugSession"/>." Implemented by
/// <see cref="DebugSession.TryEvalMemberCall"/> against the live ICorDebug surface; testable
/// substrate consumers can supply a fake.</summary>
public interface IMemberResolver
{
    /// <summary>Resolve <c><paramref name="thisLocalName"/>.<paramref name="memberName"/></c> at the
    /// current stop. The operand is named by a local variable; the member is a property whose getter
    /// is func-eval'd on the operand's runtime type (probe 24's contract). Valid only while stopped.
    /// Returns the eval outcome (<see cref="EvalStatus.Completed"/> + <paramref name="result"/> set
    /// on success; other statuses indicate setup / timeout / exception).</summary>
    EvalStatus TryEvalMemberCall(string thisLocalName, string memberName, TimeSpan timeout, out ArgumentValue result);
}
