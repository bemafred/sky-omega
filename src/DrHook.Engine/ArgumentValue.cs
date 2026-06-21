namespace SkyOmega.DrHook.Engine;

/// <summary>One argument of a stack frame. <see cref="ElementType"/> is the raw CorElementType
/// code (e.g. 0x08 = I4, 0x0A = I8, 0x12 = Class). <see cref="RawValue"/> holds the CLR-typed
/// boxed primitive — <see cref="int"/> for I4, <see cref="long"/> for I8, <see cref="bool"/> for
/// BOOLEAN, <see cref="double"/> for R8, etc. — and is null for object references and other
/// non-generic values. The boxed type matches the CorElementType so an expression compiler can
/// translate identifiers into typed <see cref="System.Linq.Expressions.Expression"/> leaves
/// without per-evaluator type-juggling. <see cref="StringValue"/> carries the rendered contents
/// when the value is a reference to a <c>System.String</c> (finding 44 / probe 35); null for
/// non-strings. <see cref="Fields"/> holds ONE level of children (immediate fields/elements) when
/// the caller requested <c>depth &gt; 0</c> and this value is an object/array; null otherwise.
/// <see cref="HasChildren"/> flags an object/array node as expandable — navigate deeper one bounded
/// level at a time via <see cref="DebugSession.ExpandArgument"/> (lazy; the eager multi-level walk
/// faulted in the runtime's unwinder at scale, ADR-007).</summary>
public readonly record struct ArgumentValue(
    int ElementType,
    object? RawValue,
    string? StringValue = null,
    IReadOnlyList<FieldValue>? Fields = null,
    bool HasChildren = false);

/// <summary>A named local variable at a stop: its source name (from the PDB) plus the same
/// CorElementType + CLR-typed boxed primitive + optional rendered string content + optional
/// fields as <see cref="ArgumentValue"/>. <see cref="RawValue"/> is null for object references
/// and locals not available at the current IL offset; otherwise its runtime type matches
/// <see cref="ElementType"/> (I4 → int, R8 → double, BOOLEAN → bool, …).
/// <see cref="StringValue"/> is null for non-strings; <see cref="Fields"/> holds ONE level of
/// children when <see cref="DebugSession.GetLocals"/> was called with <c>depth &gt; 0</c> and the
/// local is an object/array. <see cref="HasChildren"/> flags it as expandable — go deeper via
/// <see cref="DebugSession.ExpandLocal"/> (lazy navigation, ADR-007).</summary>
public readonly record struct LocalValue(
    string Name,
    int ElementType,
    object? RawValue,
    string? StringValue = null,
    IReadOnlyList<FieldValue>? Fields = null,
    bool HasChildren = false);

/// <summary>An instance field on an object value. Same shape as <see cref="LocalValue"/> — a
/// name + CLR-typed boxed primitive + optional rendered string + a <see cref="HasChildren"/> flag.
/// <see cref="RawValue"/>'s boxed type matches <see cref="ElementType"/>. <see cref="Fields"/> is
/// null in the lazy model (one level per read); when <see cref="HasChildren"/> is set, expand this
/// node on demand via <see cref="DebugSession.ExpandLocal"/> / <see cref="DebugSession.ExpandArgument"/>
/// rather than walking eagerly (ADR-007).</summary>
public readonly record struct FieldValue(
    string Name,
    int ElementType,
    object? RawValue,
    string? StringValue = null,
    IReadOnlyList<FieldValue>? Fields = null,
    bool HasChildren = false);

/// <summary>Read-only snapshot of a stop's inspectable state, passed to a conditional-breakpoint
/// predicate. A C#-expression front end (e.g. a Roslyn walker) resolves identifiers against this.</summary>
public interface IEvalContext
{
    /// <summary>Named local variables of the active frame at the stop.</summary>
    IReadOnlyList<LocalValue> Locals { get; }

    /// <summary>Argument values of the active frame at the stop (arg 0 is <c>this</c> for instance methods).</summary>
    IReadOnlyList<ArgumentValue> Arguments { get; }
}
