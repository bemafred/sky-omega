// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Diagnostics;

/// <summary>
/// A collection of diagnostics with zero-allocation storage using pooled arrays.
/// </summary>
/// <remarks>
/// This is a ref struct that must be disposed to return pooled arrays.
/// Arguments for diagnostic messages are stored in a shared char buffer.
/// </remarks>
public ref struct DiagnosticBag
{
    private const int InitialDiagnosticCapacity = 8;
    private const int InitialArgBufferSize = 256;

    private Diagnostic[]? _diagnostics;
    private char[]? _argBuffer;
    private int _count;
    private int _argPosition;

    /// <summary>
    /// Number of diagnostics in the bag.
    /// </summary>
    public readonly int Count => _count;

    /// <summary>
    /// Returns true if the bag contains any errors (not just warnings/info).
    /// </summary>
    public readonly bool HasErrors
    {
        get
        {
            for (int i = 0; i < _count; i++)
            {
                if (_diagnostics![i].IsError)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Returns true if the bag is empty.
    /// </summary>
    public readonly bool IsEmpty => _count == 0;

    /// <summary>
    /// Gets a diagnostic by index.
    /// </summary>
    public readonly Diagnostic this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                ThrowIndexOutOfRange();
            return _diagnostics![index];
        }
    }

    /// <summary>
    /// Adds a diagnostic with no arguments.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int code, SourceSpan span)
    {
        EnsureDiagnosticCapacity();
        _diagnostics![_count++] = new Diagnostic(code, span);
    }

    /// <summary>
    /// Adds a diagnostic with one string argument.
    /// </summary>
    public void Add(int code, SourceSpan span, ReadOnlySpan<char> arg0)
    {
        EnsureDiagnosticCapacity();
        var argOffset = _argPosition;
        WriteArg(arg0);

        _diagnostics![_count++] = new Diagnostic(
            code, span, argOffset, argCount: 1, argLength: arg0.Length);
    }

    /// <summary>
    /// Adds a diagnostic with two string arguments.
    /// </summary>
    public void Add(int code, SourceSpan span, ReadOnlySpan<char> arg0, ReadOnlySpan<char> arg1)
    {
        EnsureDiagnosticCapacity();
        var argOffset = _argPosition;
        WriteArg(arg0);
        WriteArg(arg1);

        _diagnostics![_count++] = new Diagnostic(
            code, span, argOffset, argCount: 2, argLength: arg0.Length + arg1.Length + 1);
    }

    /// <summary>
    /// Adds a diagnostic with three string arguments.
    /// </summary>
    public void Add(int code, SourceSpan span, ReadOnlySpan<char> arg0, ReadOnlySpan<char> arg1, ReadOnlySpan<char> arg2)
    {
        EnsureDiagnosticCapacity();
        var argOffset = _argPosition;
        WriteArg(arg0);
        WriteArg(arg1);
        WriteArg(arg2);

        _diagnostics![_count++] = new Diagnostic(
            code, span, argOffset, argCount: 3, argLength: arg0.Length + arg1.Length + arg2.Length + 2);
    }

    /// <summary>
    /// Adds a diagnostic with a related span (e.g., "previously defined here").
    /// </summary>
    public void AddWithRelated(int code, SourceSpan span, SourceSpan relatedSpan, ReadOnlySpan<char> arg0 = default)
    {
        EnsureDiagnosticCapacity();
        var argOffset = _argPosition;
        byte argCount = 0;
        int argLength = 0;

        if (!arg0.IsEmpty)
        {
            WriteArg(arg0);
            argCount = 1;
            argLength = arg0.Length;
        }

        _diagnostics![_count++] = new Diagnostic(
            code, span, relatedSpan, argOffset, argCount, argLength);
    }

    /// <summary>
    /// Gets the argument string for a diagnostic at the specified argument index.
    /// </summary>
    /// <param name="diagnostic">The diagnostic to get arguments for.</param>
    /// <param name="argIndex">The argument index (0-based).</param>
    /// <returns>The argument string, or empty if out of range.</returns>
    public readonly ReadOnlySpan<char> GetArg(in Diagnostic diagnostic, int argIndex)
    {
        if (argIndex >= diagnostic.ArgCount || _argBuffer == null)
            return ReadOnlySpan<char>.Empty;

        var span = _argBuffer.AsSpan(diagnostic.ArgOffset, diagnostic.ArgLength);

        // Arguments are null-separated in the buffer
        int start = 0;
        for (int i = 0; i < argIndex; i++)
        {
            var nullPos = span.Slice(start).IndexOf('\0');
            if (nullPos < 0)
                return ReadOnlySpan<char>.Empty;
            start += nullPos + 1;
        }

        var remaining = span.Slice(start);
        var end = remaining.IndexOf('\0');
        return end < 0 ? remaining : remaining.Slice(0, end);
    }

    /// <summary>
    /// Clears all diagnostics without returning pooled arrays.
    /// </summary>
    public void Clear()
    {
        _count = 0;
        _argPosition = 0;
    }

    /// <summary>
    /// Returns pooled arrays and resets the bag.
    /// </summary>
    public void Dispose()
    {
        if (_diagnostics != null)
        {
            ArrayPool<Diagnostic>.Shared.Return(_diagnostics);
            _diagnostics = null;
        }

        if (_argBuffer != null)
        {
            ArrayPool<char>.Shared.Return(_argBuffer);
            _argBuffer = null;
        }

        _count = 0;
        _argPosition = 0;
    }

    /// <summary>
    /// Returns an enumerator for foreach support.
    /// </summary>
    public readonly Enumerator GetEnumerator() => new(this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureDiagnosticCapacity()
    {
        _diagnostics ??= ArrayPool<Diagnostic>.Shared.Rent(InitialDiagnosticCapacity);

        if (_count >= _diagnostics.Length)
        {
            var newSize = _diagnostics.Length * 2;
            var newArray = ArrayPool<Diagnostic>.Shared.Rent(newSize);
            _diagnostics.AsSpan(0, _count).CopyTo(newArray);
            ArrayPool<Diagnostic>.Shared.Return(_diagnostics);
            _diagnostics = newArray;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteArg(ReadOnlySpan<char> arg)
    {
        if (arg.IsEmpty)
            return;

        // Add null separator if not first arg at current position
        var neededSize = _argPosition + arg.Length + 1;
        EnsureArgCapacity(neededSize);

        arg.CopyTo(_argBuffer.AsSpan(_argPosition));
        _argPosition += arg.Length;

        // Null terminate
        _argBuffer![_argPosition++] = '\0';
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureArgCapacity(int needed)
    {
        _argBuffer ??= ArrayPool<char>.Shared.Rent(InitialArgBufferSize);

        if (needed > _argBuffer.Length)
        {
            var newSize = Math.Max(_argBuffer.Length * 2, needed + 256);
            var newArray = ArrayPool<char>.Shared.Rent(newSize);
            _argBuffer.AsSpan(0, _argPosition).CopyTo(newArray);
            ArrayPool<char>.Shared.Return(_argBuffer);
            _argBuffer = newArray;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowIndexOutOfRange()
        => throw new ArgumentOutOfRangeException("index");

    /// <summary>
    /// Enumerator for iterating over diagnostics.
    /// </summary>
    public ref struct Enumerator
    {
        private readonly DiagnosticBag _bag;
        private int _index;

        internal Enumerator(DiagnosticBag bag)
        {
            _bag = bag;
            _index = -1;
        }

        public readonly Diagnostic Current => _bag[_index];

        public bool MoveNext()
        {
            _index++;
            return _index < _bag._count;
        }
    }
}
