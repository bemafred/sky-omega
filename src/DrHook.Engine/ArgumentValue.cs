namespace SkyOmega.DrHook.Engine;

/// <summary>One argument of a stack frame. <see cref="ElementType"/> is the raw CorElementType
/// code (e.g. 0x08 = I4, 0x0A = I8, 0x12 = Class). <see cref="RawValue"/> holds the primitive
/// bits for a generic (primitive) value, and is null for object references and other non-generic
/// values. <see cref="StringValue"/> carries the rendered contents when the value is a reference
/// to a <c>System.String</c> (finding 44 / probe 35); null for non-strings.</summary>
public readonly record struct ArgumentValue(int ElementType, long? RawValue, string? StringValue = null);

/// <summary>A named local variable at a stop: its source name (from the PDB) plus the same
/// CorElementType + raw primitive bits + optional rendered string content as
/// <see cref="ArgumentValue"/>. <see cref="RawValue"/> is null for object references and locals
/// not available at the current IL offset; <see cref="StringValue"/> is null for non-strings.</summary>
public readonly record struct LocalValue(string Name, int ElementType, long? RawValue, string? StringValue = null);

/// <summary>Read-only snapshot of a stop's inspectable state, passed to a conditional-breakpoint
/// predicate. A C#-expression front end (e.g. a Roslyn walker) resolves identifiers against this.</summary>
public interface IEvalContext
{
    /// <summary>Named local variables of the active frame at the stop.</summary>
    IReadOnlyList<LocalValue> Locals { get; }

    /// <summary>Argument values of the active frame at the stop (arg 0 is <c>this</c> for instance methods).</summary>
    IReadOnlyList<ArgumentValue> Arguments { get; }
}
