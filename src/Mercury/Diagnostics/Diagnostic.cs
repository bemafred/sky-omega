// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Represents a single diagnostic (error, warning, or info) with source location.
/// </summary>
/// <remarks>
/// This is a readonly struct designed for zero-allocation storage in a <see cref="DiagnosticBag"/>.
/// Message formatting is deferred until needed, using argument offsets into a shared buffer.
/// </remarks>
internal readonly struct Diagnostic : IEquatable<Diagnostic>
{
    /// <summary>
    /// The diagnostic code (see <see cref="DiagnosticCode"/>).
    /// </summary>
    public readonly int Code;

    /// <summary>
    /// The source location where the diagnostic occurred.
    /// </summary>
    public readonly SourceSpan Span;

    /// <summary>
    /// Optional related location (e.g., "previously defined here").
    /// </summary>
    public readonly SourceSpan RelatedSpan;

    /// <summary>
    /// Offset into the argument buffer where this diagnostic's arguments start.
    /// </summary>
    internal readonly int ArgOffset;

    /// <summary>
    /// Number of arguments for this diagnostic's message template.
    /// </summary>
    public readonly byte ArgCount;

    /// <summary>
    /// Total length of arguments in the buffer (in chars).
    /// </summary>
    internal readonly int ArgLength;

    /// <summary>
    /// Creates a diagnostic with no arguments.
    /// </summary>
    public Diagnostic(int code, SourceSpan span)
    {
        Code = code;
        Span = span;
        RelatedSpan = default;
        ArgOffset = 0;
        ArgCount = 0;
        ArgLength = 0;
    }

    /// <summary>
    /// Creates a diagnostic with argument buffer information.
    /// </summary>
    internal Diagnostic(int code, SourceSpan span, int argOffset, byte argCount, int argLength)
    {
        Code = code;
        Span = span;
        RelatedSpan = default;
        ArgOffset = argOffset;
        ArgCount = argCount;
        ArgLength = argLength;
    }

    /// <summary>
    /// Creates a diagnostic with a related span.
    /// </summary>
    internal Diagnostic(int code, SourceSpan span, SourceSpan relatedSpan, int argOffset, byte argCount, int argLength)
    {
        Code = code;
        Span = span;
        RelatedSpan = relatedSpan;
        ArgOffset = argOffset;
        ArgCount = argCount;
        ArgLength = argLength;
    }

    /// <summary>
    /// Gets the severity of this diagnostic.
    /// </summary>
    public DiagnosticSeverity Severity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => DiagnosticCode.GetSeverity(Code);
    }

    /// <summary>
    /// Returns true if this is an error (not warning or info).
    /// </summary>
    public bool IsError
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => DiagnosticCode.IsError(Code);
    }

    /// <summary>
    /// Returns true if this diagnostic has a related span.
    /// </summary>
    public bool HasRelatedSpan => RelatedSpan.Length > 0 || RelatedSpan.Line > 0;

    /// <summary>
    /// Gets the formatted code string (e.g., "E1001").
    /// </summary>
    public string CodeString => DiagnosticCode.FormatCode(Code);

    public bool Equals(Diagnostic other)
        => Code == other.Code
        && Span == other.Span
        && RelatedSpan == other.RelatedSpan
        && ArgOffset == other.ArgOffset
        && ArgCount == other.ArgCount;

    public override bool Equals(object? obj)
        => obj is Diagnostic other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Code, Span, RelatedSpan);

    public static bool operator ==(Diagnostic left, Diagnostic right)
        => left.Equals(right);

    public static bool operator !=(Diagnostic left, Diagnostic right)
        => !left.Equals(right);

    public override string ToString()
        => $"{CodeString} at {Span}";
}
