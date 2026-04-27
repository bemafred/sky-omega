using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// CRC-32 for bzip2 streams. Standard CRC-32-IEEE polynomial 0x04C11DB7 with
/// **non-reflected** input/output bit ordering — distinct from the reflected variant
/// used by gzip/zlib (and from .NET's <see cref="System.IO.Hashing.Crc32"/>). Slicing-by-8
/// implementation: 8 KB of precomputed tables, ~1 ns/byte on contemporary hardware.
/// ADR-036 Decision 2.
/// </summary>
/// <remarks>
/// <para>
/// Initial value: 0xFFFFFFFF. Per-byte update: <c>crc = (crc &lt;&lt; 8) ^ T0[(crc &gt;&gt; 24) ^ byte]</c>.
/// Finalize by inverting all bits (<c>~crc</c>). Hardware CRC32 instructions on ARM (CRC32X)
/// and x86 (CRC32) are reflected variants and not directly applicable here without bit-reversal —
/// pure software with cache-resident tables is the right shape.
/// </para>
/// <para>
/// Type-named so it cannot be accidentally substituted for the reflected variant. Confusing the
/// two produces silent corruption that the per-block + stream CRC checks would otherwise catch.
/// </para>
/// </remarks>
internal static class BZip2Crc32
{
    public const uint InitialValue = 0xFFFFFFFFu;
    private const uint Polynomial = 0x04C11DB7u;

    // Slicing-by-8 tables. Layout: T0[b] = CRC of one byte b applied to zero state.
    // Tk[b] = CRC of k zero-bytes followed by b applied to zero state. Combined into
    // a single XOR fan-in, eight bytes resolve in eight table lookups + a final XOR.
    private static readonly uint[] T0 = new uint[256];
    private static readonly uint[] T1 = new uint[256];
    private static readonly uint[] T2 = new uint[256];
    private static readonly uint[] T3 = new uint[256];
    private static readonly uint[] T4 = new uint[256];
    private static readonly uint[] T5 = new uint[256];
    private static readonly uint[] T6 = new uint[256];
    private static readonly uint[] T7 = new uint[256];

    static BZip2Crc32()
    {
        for (int b = 0; b < 256; b++)
        {
            uint c = (uint)b << 24;
            for (int i = 0; i < 8; i++)
                c = (c & 0x80000000u) != 0 ? (c << 1) ^ Polynomial : c << 1;
            T0[b] = c;
        }
        for (int b = 0; b < 256; b++)
        {
            uint c = T0[b];
            T1[b] = (c << 8) ^ T0[(c >> 24) & 0xFF]; c = T1[b];
            T2[b] = (c << 8) ^ T0[(c >> 24) & 0xFF]; c = T2[b];
            T3[b] = (c << 8) ^ T0[(c >> 24) & 0xFF]; c = T3[b];
            T4[b] = (c << 8) ^ T0[(c >> 24) & 0xFF]; c = T4[b];
            T5[b] = (c << 8) ^ T0[(c >> 24) & 0xFF]; c = T5[b];
            T6[b] = (c << 8) ^ T0[(c >> 24) & 0xFF]; c = T6[b];
            T7[b] = (c << 8) ^ T0[(c >> 24) & 0xFF];
        }
    }

    /// <summary>Update <paramref name="crc"/> by the bytes in <paramref name="data"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        int i = 0;
        // 8-byte slicing: process aligned 8-byte chunks via the eight-table fan-in.
        // The outer loop bound and stride keep the tables L1-resident and the input
        // sequential — both critical for the ~1 ns/byte target on current hardware.
        while (i + 8 <= data.Length)
        {
            uint hi = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i, 4));
            uint lo = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i + 4, 4));
            uint c = crc ^ hi;
            crc = T7[(c >> 24) & 0xFF]
                ^ T6[(c >> 16) & 0xFF]
                ^ T5[(c >> 8) & 0xFF]
                ^ T4[c & 0xFF]
                ^ T3[(lo >> 24) & 0xFF]
                ^ T2[(lo >> 16) & 0xFF]
                ^ T1[(lo >> 8) & 0xFF]
                ^ T0[lo & 0xFF];
            i += 8;
        }
        // Tail: byte-at-a-time for the < 8 remainder.
        for (; i < data.Length; i++)
            crc = (crc << 8) ^ T0[((crc >> 24) ^ data[i]) & 0xFF];
        return crc;
    }

    /// <summary>Finalize a streaming CRC: bzip2 inverts all bits at the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Finalize(uint crc) => ~crc;

    /// <summary>One-shot CRC of a buffer (initial → update → finalize).</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Finalize(Update(InitialValue, data));
}
