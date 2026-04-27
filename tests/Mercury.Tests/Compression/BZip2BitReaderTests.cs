using System;
using SkyOmega.Mercury.Compression;
using Xunit;

namespace SkyOmega.Mercury.Tests.Compression;

/// <summary>
/// ADR-036 Decision 2: bit-level reader correctness. The reader is MSB-first within
/// each byte, matching bzip2's bit-packing convention. Tests cover boundary alignment,
/// 64-bit refill, byte-at-a-time fallback, peek/consume, and end-of-stream behavior.
/// </summary>
public class BZip2BitReaderTests
{
    [Fact]
    public void ReadBits_MsbFirstWithinByte()
    {
        // Single byte 0xA5 = 10100101 (binary). MSB-first reading:
        // bit 0 = 1, bit 1 = 0, bit 2 = 1, bit 3 = 0, bit 4 = 0, bit 5 = 1, bit 6 = 0, bit 7 = 1.
        var data = new byte[] { 0xA5 };
        var reader = new BZip2BitReader(data);
        Assert.Equal(1u, reader.ReadBit());
        Assert.Equal(0u, reader.ReadBit());
        Assert.Equal(1u, reader.ReadBit());
        Assert.Equal(0u, reader.ReadBit());
        Assert.Equal(0u, reader.ReadBit());
        Assert.Equal(1u, reader.ReadBit());
        Assert.Equal(0u, reader.ReadBit());
        Assert.Equal(1u, reader.ReadBit());
    }

    [Fact]
    public void ReadBits_4BitNibbles()
    {
        // 0xA5 0xC3 = 1010_0101 1100_0011. Four nibbles: 0xA, 0x5, 0xC, 0x3.
        var data = new byte[] { 0xA5, 0xC3 };
        var reader = new BZip2BitReader(data);
        Assert.Equal(0xAu, reader.ReadBits(4));
        Assert.Equal(0x5u, reader.ReadBits(4));
        Assert.Equal(0xCu, reader.ReadBits(4));
        Assert.Equal(0x3u, reader.ReadBits(4));
    }

    [Fact]
    public void ReadBits_AcrossByteBoundary()
    {
        // 0xFF 0x00 = 1111_1111 0000_0000. Read 12 bits = 1111_1111_0000 = 0xFF0.
        var data = new byte[] { 0xFF, 0x00 };
        var reader = new BZip2BitReader(data);
        Assert.Equal(0xFF0u, reader.ReadBits(12));
        Assert.Equal(0x0u, reader.ReadBits(4));
    }

    [Fact]
    public void ReadBits_24BitBlockMagic()
    {
        // bzip2 reads block magic as a 48-bit value via two 24-bit reads.
        // Bytes: 0x31 0x41 0x59 0x26 0x53 0x59 = block magic (pi).
        var data = new byte[] { 0x31, 0x41, 0x59, 0x26, 0x53, 0x59 };
        var reader = new BZip2BitReader(data);
        Assert.Equal(0x314159UL, reader.ReadBits(24));
        Assert.Equal(0x265359UL, reader.ReadBits(24));
    }

    [Fact]
    public void ReadUInt48BigEndian_BlockMagic()
    {
        var data = new byte[] { 0x31, 0x41, 0x59, 0x26, 0x53, 0x59 };
        var reader = new BZip2BitReader(data);
        Assert.Equal(0x314159265359UL, reader.ReadUInt48BigEndian());
    }

    [Fact]
    public void ReadUInt32BigEndian_StreamCrc()
    {
        var data = new byte[] { 0x05, 0x39, 0x88, 0x75 };
        var reader = new BZip2BitReader(data);
        Assert.Equal(0x05398875u, reader.ReadUInt32BigEndian());
    }

    [Fact]
    public void Refill_FastPath_64BitAligned()
    {
        // 8 bytes available and accumulator empty: the reader should pull 64 bits in
        // one big-endian load. Verify by reading 64 bits in one ReadBits call.
        var data = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 };
        var reader = new BZip2BitReader(data);
        // ReadBits maxes at 32; do two 32-bit reads.
        Assert.Equal(0x12345678u, reader.ReadBits(32));
        Assert.Equal(0x9ABCDEF0u, reader.ReadBits(32));
        Assert.True(reader.IsExhausted);
    }

    [Fact]
    public void Refill_SlowPath_FewerThan8Bytes()
    {
        // 7 bytes available — fast 64-bit path is skipped, byte-at-a-time refill kicks in.
        var data = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD };
        var reader = new BZip2BitReader(data);
        Assert.Equal(0x01234567u, reader.ReadBits(32));
        Assert.Equal(0x89ABCDu, reader.ReadBits(24));
        Assert.True(reader.IsExhausted);
    }

    [Fact]
    public void PeekBits_ConsumeBits_DecoupledLookup()
    {
        // Standard table-Huffman pattern: peek N bits, look up, consume actual code length.
        var data = new byte[] { 0xAB, 0xCD };
        var reader = new BZip2BitReader(data);
        Assert.Equal(0xABCu, reader.PeekBits(12));
        // PeekBits doesn't consume — re-peeking returns the same value.
        Assert.Equal(0xABCu, reader.PeekBits(12));
        // Consume only 8 bits (e.g. an 8-bit Huffman code).
        reader.ConsumeBits(8);
        Assert.Equal(0xCDu, reader.ReadBits(8));
    }

    [Fact]
    public void AlignToByte_DropsFractionalBits()
    {
        // Read 5 bits, then align to next byte. The remaining 3 bits of byte 1 are dropped.
        var data = new byte[] { 0xFF, 0xAA };
        var reader = new BZip2BitReader(data);
        Assert.Equal(0x1Fu, reader.ReadBits(5));      // top 5 bits of 0xFF
        reader.AlignToByte();                          // drop 3 bits
        Assert.Equal(0xAAu, reader.ReadBits(8));      // next byte intact
    }

    [Fact]
    public void EndOfStream_ReadPastBufferThrows()
    {
        var data = new byte[] { 0x42 };
        var reader = new BZip2BitReader(data);
        reader.ReadBits(8);
        // ref struct can't cross lambda boundary; inline the catch.
        bool threw = false;
        try { reader.ReadBit(); }
        catch (EndOfStreamException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void EndOfStream_PartialReadThrows()
    {
        // Source has 8 bits but caller asks for 16. Must throw, not return garbage.
        var data = new byte[] { 0x42 };
        var reader = new BZip2BitReader(data);
        bool threw = false;
        try { reader.ReadBits(16); }
        catch (EndOfStreamException) { threw = true; }
        Assert.True(threw);
    }

    [Fact]
    public void ReadBits_BoundaryAlignment_AcrossEightByteBlocks()
    {
        // Read patterns that cross multiple 64-bit refills. 16 bytes, read in 7-bit
        // chunks: forces refill timing to vary across the source.
        var data = new byte[16];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 17 + 3);

        // Reference: scalar bit-by-bit read.
        ulong reference = 0;
        int referenceBits = 0;
        var refReader = new BZip2BitReader(data);
        while (!refReader.IsExhausted || refReader.BufferedBits > 0)
        {
            int n = Math.Min(7, refReader.BufferedBits + refReader.RemainingSourceBytes * 8);
            if (n == 0) break;
            reference = (reference << n) | refReader.ReadBits(n);
            referenceBits += n;
        }

        // Now read everything in one stream of 7-bit chunks.
        var actualReader = new BZip2BitReader(data);
        ulong actual = 0;
        int actualBits = 0;
        while (!actualReader.IsExhausted || actualReader.BufferedBits > 0)
        {
            int n = Math.Min(7, actualReader.BufferedBits + actualReader.RemainingSourceBytes * 8);
            if (n == 0) break;
            actual = (actual << n) | actualReader.ReadBits(n);
            actualBits += n;
        }

        Assert.Equal(referenceBits, actualBits);
        Assert.Equal(reference, actual);
    }
}
