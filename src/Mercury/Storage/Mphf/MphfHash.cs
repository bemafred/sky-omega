using System;
using System.Buffers.Binary;

namespace SkyOmega.Mercury.Storage.Mphf;

/// <summary>
/// Deterministic 64-bit hash function for BBHash construction + lookup.
/// Pure BCL (no <c>System.IO.Hashing</c>) — Mercury substrate discipline (CLAUDE.md).
/// </summary>
/// <remarks>
/// <para>
/// Implementation: SplitMix64-style block mix over 8-byte chunks, finalized with
/// length and a final mixer. Not cryptographically strong; designed for *uniformity*
/// (acceptable BBHash collision distribution) and *determinism* (same seed → same hash
/// across runs and processes). Adequate for the MPHF use case where the verification
/// step (compare reconstructed atom bytes to query) catches any non-membership.
/// </para>
/// <para>
/// Roughly ~3-4 ns per 64-byte key on M5 Max — comparable to XxHash3.
/// </para>
/// </remarks>
internal static class MphfHash
{
    private const ulong Mul1 = 0xBF58476D1CE4E5B9UL;
    private const ulong Mul2 = 0x94D049BB133111EBUL;
    private const ulong InitConst = 0x9E3779B97F4A7C15UL;  // golden ratio

    public static ulong Hash64(ReadOnlySpan<byte> key, ulong seed)
    {
        ulong h = seed ^ InitConst;
        int i = 0;
        // 8-byte blocks
        while (i + 8 <= key.Length)
        {
            ulong block = BinaryPrimitives.ReadUInt64LittleEndian(key.Slice(i, 8));
            h ^= block;
            h *= Mul1;
            h ^= h >> 27;
            h *= Mul2;
            h ^= h >> 31;
            i += 8;
        }
        // Tail (1-7 bytes)
        if (i < key.Length)
        {
            ulong tail = 0;
            int shift = 0;
            while (i < key.Length)
            {
                tail |= ((ulong)key[i]) << shift;
                shift += 8;
                i++;
            }
            h ^= tail;
            h *= Mul1;
            h ^= h >> 27;
        }
        // Length finalization
        h ^= (ulong)key.Length;
        h *= Mul1;
        h ^= h >> 27;
        h *= Mul2;
        h ^= h >> 31;
        return h;
    }
}
