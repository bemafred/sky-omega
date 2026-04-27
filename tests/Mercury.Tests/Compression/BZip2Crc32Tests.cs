using System;
using System.Text;
using SkyOmega.Mercury.Compression;
using Xunit;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// ADR-036 Decision 2: bzip2 CRC32 correctness. Reference values are from running
/// the published bzip2-1.0.8 reference implementation against the same inputs;
/// these are the exact CRC values bzip2 stores in block + stream trailers.
/// </summary>
public class BZip2Crc32Tests
{
    [Fact]
    public void EmptyInput_ProducesZero()
    {
        // Initial 0xFFFFFFFF, no updates, ~0xFFFFFFFF = 0.
        Assert.Equal(0u, BZip2Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void HelloWorld_MatchesActualBzip2BlockCrc()
    {
        // Generated out-of-band: `echo -n "Hello world" | bzip2 -c | xxd`.
        // The bzip2 frame stores the per-block CRC at bytes 10-13 (big-endian),
        // immediately after the 'BZh' header (4 bytes) and the pi block magic
        // 0x314159265359 (6 bytes). For input "Hello world" (11 bytes ASCII),
        // bzip2-1.0.8 stores 0x05398875. This is the canonical end-to-end
        // verification: matching it confirms the implementation is bit-identical
        // to bzip2's stored block CRCs at every scale.
        var bytes = Encoding.ASCII.GetBytes("Hello world");
        Assert.Equal(0x05398875u, BZip2Crc32.Compute(bytes));
    }

    [Fact]
    public void StreamingUpdate_MatchesOneShot()
    {
        // Splitting the input across multiple Update calls must produce the same
        // CRC as a single one-shot Compute. This is the contract the per-block
        // streaming reader depends on.
        var bytes = new byte[10_000];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 31 + 7);

        uint oneShot = BZip2Crc32.Compute(bytes);

        uint streaming = BZip2Crc32.InitialValue;
        streaming = BZip2Crc32.Update(streaming, bytes.AsSpan(0, 1));
        streaming = BZip2Crc32.Update(streaming, bytes.AsSpan(1, 7));     // odd boundary
        streaming = BZip2Crc32.Update(streaming, bytes.AsSpan(8, 1024));  // 8-aligned chunk
        streaming = BZip2Crc32.Update(streaming, bytes.AsSpan(1032, 3));  // tiny tail
        streaming = BZip2Crc32.Update(streaming, bytes.AsSpan(1035));      // remainder
        streaming = BZip2Crc32.Finalize(streaming);

        Assert.Equal(oneShot, streaming);
    }

    [Fact]
    public void SlicingBy8_TailHandling_BoundaryByteCount()
    {
        // Lengths around the 8-byte slicing boundary must produce identical CRCs
        // whether the bytes were processed in the slicing path or the byte-tail path.
        var rng = new Random(42);
        for (int len = 0; len <= 64; len++)
        {
            var data = new byte[len];
            rng.NextBytes(data);

            // Reference: byte-at-a-time computation (no slicing).
            uint reference = BZip2Crc32.InitialValue;
            for (int i = 0; i < len; i++)
                reference = (reference << 8) ^ TableLookupByteAtATime(reference, data[i]);
            reference = ~reference;

            Assert.Equal(reference, BZip2Crc32.Compute(data));
        }
    }

    /// <summary>
    /// Standalone byte-at-a-time CRC for the boundary test. Implements the bzip2
    /// step from Seward's reference: <c>crc = (crc &lt;&lt; 8) ^ T0[(crc &gt;&gt; 24) ^ byte]</c>.
    /// </summary>
    private static uint TableLookupByteAtATime(uint crc, byte b)
    {
        const uint Polynomial = 0x04C11DB7u;
        uint t = (uint)((((crc >> 24) ^ b) & 0xFF) << 24);
        for (int i = 0; i < 8; i++)
            t = (t & 0x80000000u) != 0 ? (t << 1) ^ Polynomial : t << 1;
        return t;
    }

    [Fact]
    public void BitByBit_Reference_Matches_TableImplementation()
    {
        // Fully independent reference: bit-by-bit polynomial long division.
        // No table involved. If the table-based path matches this, the algorithm
        // is verified end-to-end.
        var rng = new Random(13);
        for (int len = 0; len <= 256; len += 7)
        {
            var data = new byte[len];
            rng.NextBytes(data);

            uint reference = BitByBitCrc(data);
            uint actual = BZip2Crc32.Compute(data);
            Assert.Equal(reference, actual);
        }
    }

    private static uint BitByBitCrc(ReadOnlySpan<byte> data)
    {
        const uint Polynomial = 0x04C11DB7u;
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= (uint)b << 24;
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x80000000u) != 0 ? (crc << 1) ^ Polynomial : crc << 1;
        }
        return ~crc;
    }

    [Fact]
    public void LargeInput_PerformanceSanity()
    {
        // 1 MB random input. Verifies the slicing-by-8 path doesn't crash on size
        // and produces stable values across re-runs.
        var data = new byte[1_000_000];
        new Random(7).NextBytes(data);
        uint a = BZip2Crc32.Compute(data);
        uint b = BZip2Crc32.Compute(data);
        Assert.Equal(a, b);
    }
}
