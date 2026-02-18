// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// Represents a span of characters in source text with line/column information.
/// </summary>
/// <remarks>
/// This is a value type designed for zero-allocation error reporting.
/// Line and column are 1-based (matching editor conventions).
/// </remarks>
internal readonly struct SourceSpan : IEquatable<SourceSpan>
{
    /// <summary>
    /// Character offset from the start of the source (0-based).
    /// </summary>
    public readonly int Start;

    /// <summary>
    /// Length of the span in characters.
    /// </summary>
    public readonly int Length;

    /// <summary>
    /// Line number (1-based).
    /// </summary>
    public readonly int Line;

    /// <summary>
    /// Column number within the line (1-based).
    /// </summary>
    public readonly int Column;

    /// <summary>
    /// Creates a new source span.
    /// </summary>
    /// <param name="start">Character offset from start of source (0-based).</param>
    /// <param name="length">Length of span in characters.</param>
    /// <param name="line">Line number (1-based).</param>
    /// <param name="column">Column number (1-based).</param>
    public SourceSpan(int start, int length, int line, int column)
    {
        Start = start;
        Length = length;
        Line = line;
        Column = column;
    }

    /// <summary>
    /// Gets the end offset (exclusive) of this span.
    /// </summary>
    public int End => Start + Length;

    /// <summary>
    /// Returns true if this span has zero length.
    /// </summary>
    public bool IsEmpty => Length == 0;

    /// <summary>
    /// An empty span at position 0.
    /// </summary>
    public static SourceSpan Empty => default;

    /// <summary>
    /// Creates a span for a single character at the given position.
    /// </summary>
    public static SourceSpan SingleChar(int offset, int line, int column)
        => new(offset, 1, line, column);

    /// <summary>
    /// Creates a span covering from this span's start to another span's end.
    /// </summary>
    /// <param name="other">The span to extend to.</param>
    /// <returns>A new span covering both ranges.</returns>
    public SourceSpan ExtendTo(SourceSpan other)
    {
        if (other.Start < Start)
            return other.ExtendTo(this);

        return new SourceSpan(
            Start,
            other.End - Start,
            Line,
            Column);
    }

    /// <summary>
    /// Extracts the text covered by this span from the source.
    /// </summary>
    /// <param name="source">The full source text.</param>
    /// <returns>The substring covered by this span.</returns>
    public ReadOnlySpan<char> GetText(ReadOnlySpan<char> source)
    {
        if (Start < 0 || Start >= source.Length)
            return ReadOnlySpan<char>.Empty;

        var len = Math.Min(Length, source.Length - Start);
        return source.Slice(Start, len);
    }

    public bool Equals(SourceSpan other)
        => Start == other.Start
        && Length == other.Length
        && Line == other.Line
        && Column == other.Column;

    public override bool Equals(object? obj)
        => obj is SourceSpan other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Start, Length, Line, Column);

    public static bool operator ==(SourceSpan left, SourceSpan right)
        => left.Equals(right);

    public static bool operator !=(SourceSpan left, SourceSpan right)
        => !left.Equals(right);

    public override string ToString()
        => $"({Line}:{Column}, len={Length})";
}
