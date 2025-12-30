// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Buffers;

/// <summary>
/// Abstraction for buffer allocation strategies.
/// </summary>
/// <remarks>
/// <para>
/// Design goals:
/// - Unified buffer allocation across all Mercury components
/// - Zero-GC through ArrayPool backing
/// - RAII-style cleanup via <see cref="BufferLease{T}"/>
/// - Compile-time enforcement via ref struct constraints
/// </para>
/// <para>
/// The buffer manager replaces scattered allocations like <c>new char[1024]</c> with
/// pooled buffers that are automatically returned when disposed.
/// </para>
/// </remarks>
public interface IBufferManager
{
    /// <summary>
    /// Rents a buffer of at least the specified length.
    /// </summary>
    /// <typeparam name="T">Element type (must be unmanaged).</typeparam>
    /// <param name="minimumLength">Minimum buffer length required.</param>
    /// <returns>A lease that must be disposed to return the buffer.</returns>
    BufferLease<T> Rent<T>(int minimumLength) where T : unmanaged;

    /// <summary>
    /// Returns a previously rented buffer to the pool.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="buffer">The buffer to return.</param>
    /// <param name="clearArray">Whether to clear the buffer contents before returning.</param>
    void Return<T>(T[] buffer, bool clearArray = false) where T : unmanaged;
}

/// <summary>
/// RAII-style lease for a pooled buffer that automatically returns on dispose.
/// </summary>
/// <typeparam name="T">Element type of the buffer.</typeparam>
/// <remarks>
/// <para>
/// This is a ref struct to enforce stack-only lifetime and prevent leaks.
/// The buffer is automatically returned to the pool when <see cref="Dispose"/> is called.
/// </para>
/// <para>
/// Usage:
/// <code>
/// using var lease = bufferManager.Rent&lt;char&gt;(1024);
/// var span = lease.Span;
/// // use span...
/// // buffer automatically returned at end of scope
/// </code>
/// </para>
/// </remarks>
public ref struct BufferLease<T> where T : unmanaged
{
    private readonly IBufferManager? _manager;
    private T[]? _array;
    private readonly int _requestedLength;

    /// <summary>
    /// Creates a new buffer lease.
    /// </summary>
    internal BufferLease(IBufferManager manager, T[] array, int requestedLength)
    {
        _manager = manager;
        _array = array;
        _requestedLength = requestedLength;
    }

    /// <summary>
    /// Creates an empty lease (no buffer allocated).
    /// </summary>
    public static BufferLease<T> Empty => default;

    /// <summary>
    /// Gets a span over the usable portion of the buffer.
    /// </summary>
    /// <remarks>
    /// The span length equals the requested length, not the actual array length
    /// (which may be larger due to pool bucket sizing).
    /// </remarks>
    public readonly Span<T> Span => _array != null ? _array.AsSpan(0, _requestedLength) : Span<T>.Empty;

    /// <summary>
    /// Gets the underlying array (may be larger than requested length).
    /// </summary>
    /// <remarks>
    /// Use <see cref="Span"/> for most operations to avoid exposing extra capacity.
    /// </remarks>
    public readonly T[]? Array => _array;

    /// <summary>
    /// Gets the requested buffer length.
    /// </summary>
    public readonly int Length => _requestedLength;

    /// <summary>
    /// Gets whether this lease holds a valid buffer.
    /// </summary>
    public readonly bool IsEmpty => _array == null;

    /// <summary>
    /// Returns the buffer to the pool.
    /// </summary>
    public void Dispose()
    {
        if (_array != null && _manager != null)
        {
            _manager.Return(_array, clearArray: false);
            _array = null;
        }
    }
}

/// <summary>
/// Default buffer manager implementation using <see cref="ArrayPool{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// This implementation wraps <see cref="ArrayPool{T}.Shared"/> and is safe for
/// concurrent use from multiple threads.
/// </para>
/// <para>
/// Use <see cref="Shared"/> for the singleton instance.
/// </para>
/// </remarks>
public sealed class PooledBufferManager : IBufferManager
{
    /// <summary>
    /// Shared singleton instance.
    /// </summary>
    public static readonly PooledBufferManager Shared = new();

    private PooledBufferManager() { }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BufferLease<T> Rent<T>(int minimumLength) where T : unmanaged
    {
        if (minimumLength <= 0)
            return BufferLease<T>.Empty;

        var array = ArrayPool<T>.Shared.Rent(minimumLength);
        return new BufferLease<T>(this, array, minimumLength);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return<T>(T[] buffer, bool clearArray = false) where T : unmanaged
    {
        if (buffer != null)
            ArrayPool<T>.Shared.Return(buffer, clearArray);
    }
}

/// <summary>
/// Extension methods for buffer management.
/// </summary>
public static class BufferManagerExtensions
{
    /// <summary>
    /// Rents a char buffer of at least the specified length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BufferLease<char> RentCharBuffer(this IBufferManager manager, int minimumLength)
        => manager.Rent<char>(minimumLength);

    /// <summary>
    /// Rents a byte buffer of at least the specified length.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BufferLease<byte> RentByteBuffer(this IBufferManager manager, int minimumLength)
        => manager.Rent<byte>(minimumLength);

    /// <summary>
    /// Allocates a buffer using stackalloc for small sizes, falling back to pooled allocation.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="manager">The buffer manager.</param>
    /// <param name="length">Required length.</param>
    /// <param name="stackBuffer">Pre-allocated stack buffer (use stackalloc).</param>
    /// <param name="rentedBuffer">Output: the rented buffer if stack was too small, null otherwise.</param>
    /// <returns>A span covering the allocated space.</returns>
    /// <remarks>
    /// <para>
    /// This method enables the "small-stack, large-pool" pattern:
    /// <code>
    /// Span&lt;char&gt; stack = stackalloc char[256];
    /// var span = manager.AllocateSmart(neededLength, stack, out var rented);
    /// try
    /// {
    ///     // use span...
    /// }
    /// finally
    /// {
    ///     rented.Dispose();
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<T> AllocateSmart<T>(
        this IBufferManager manager,
        int length,
        Span<T> stackBuffer,
        out BufferLease<T> rentedBuffer) where T : unmanaged
    {
        if (length <= stackBuffer.Length)
        {
            rentedBuffer = BufferLease<T>.Empty;
            return stackBuffer.Slice(0, length);
        }

        rentedBuffer = manager.Rent<T>(length);
        return rentedBuffer.Span;
    }

    /// <summary>
    /// Rents a char buffer, using the provided stack buffer if large enough.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<char> AllocateSmartChar(
        this IBufferManager manager,
        int length,
        Span<char> stackBuffer,
        out BufferLease<char> rentedBuffer)
        => manager.AllocateSmart(length, stackBuffer, out rentedBuffer);

    /// <summary>
    /// Rents a byte buffer, using the provided stack buffer if large enough.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<byte> AllocateSmartByte(
        this IBufferManager manager,
        int length,
        Span<byte> stackBuffer,
        out BufferLease<byte> rentedBuffer)
        => manager.AllocateSmart(length, stackBuffer, out rentedBuffer);
}
