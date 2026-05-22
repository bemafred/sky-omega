namespace SkyOmega.DrHook.Engine;

/// <summary>One argument of a stack frame. <see cref="ElementType"/> is the raw CorElementType
/// code (e.g. 0x08 = I4, 0x0A = I8, 0x12 = Class). <see cref="RawValue"/> holds the primitive
/// bits for a generic (primitive) value, and is null for object references and other non-generic
/// values. A typed/named value API is a later refinement (local names need PDBs).</summary>
public readonly record struct ArgumentValue(int ElementType, long? RawValue);
