using System;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// Decodes a single bzip2 block: parses the on-wire frame, runs Huffman + MTF + RLE2 →
/// BWT inverse → RLE1, and exposes the result as a byte span. ADR-036 Decision 2.
/// </summary>
/// <remarks>
/// <para>
/// bzip2 frame layout per Seward 1996, decompress.c. Block magic 0x314159265359 (pi)
/// starts each block; 0x177245385090 (sqrt(pi)) signals end-of-stream — handled by the
/// owning stream, not here. Frame fields:
/// </para>
/// <list type="number">
///   <item><description>32-bit per-block CRC of the decompressed output (returned to caller for verification)</description></item>
///   <item><description>1-bit randomized flag (must be 0 — randomized blocks are deprecated and not supported)</description></item>
///   <item><description>24-bit BWT origin pointer</description></item>
///   <item><description>16-bit symbol-group map + per-set-group 16-bit byte map → which bytes appear</description></item>
///   <item><description>3-bit numTables (2–6); 15-bit numSelectors</description></item>
///   <item><description>numSelectors × unary MTF-encoded selector indices</description></item>
///   <item><description>Per table: 5-bit start-length + per-symbol delta-encoded code lengths</description></item>
///   <item><description>Encoded symbol stream until EOB</description></item>
/// </list>
/// <para>
/// All scratch buffers preallocated at construction; per-block decode does not allocate.
/// MTF state is held in the embedded <see cref="MoveToFrontInverse"/> struct (256-byte
/// inline array). RLE1 state in <see cref="Rle1Inverse"/>. BWT inverse owns its own
/// 3.6 MB int[] for the maximum 900 KB block size.
/// </para>
/// </remarks>
internal sealed class BZip2BlockReader
{
    public const ulong BlockMagic = 0x314159265359UL;        // pi prefix
    public const ulong EndOfStreamMagic = 0x177245385090UL;  // sqrt(pi) prefix

    private const int RUNA = 0;
    private const int RUNB = 1;
    private const int MaxTables = 6;
    private const int MaxSelectors = 18002;  // bzip2 spec maximum
    private const int GroupSize = 50;        // symbols per selector group

    private readonly HuffmanDecoder[] _decoders;
    private readonly byte[][] _codeLengths;  // [table][symbol]
    private readonly byte[] _selectors;
    private readonly byte[] _alphabet;
    private readonly byte[] _bwtInput;
    private readonly byte[] _bwtOutput;
    private readonly BurrowsWheelerInverse _bwt;
    private MoveToFrontInverse _mtf;
    private Rle1Inverse _rle1;

    private int _bwtOutputLength;
    private int _bwtOutputCursor;

    public BZip2BlockReader()
    {
        _decoders = new HuffmanDecoder[MaxTables];
        for (int i = 0; i < MaxTables; i++) _decoders[i] = new HuffmanDecoder();
        _codeLengths = new byte[MaxTables][];
        for (int i = 0; i < MaxTables; i++) _codeLengths[i] = new byte[HuffmanDecoder.MaxAlphabetSize];
        _selectors = new byte[MaxSelectors];
        _alphabet = new byte[256];
        _bwtInput = new byte[BurrowsWheelerInverse.MaxBlockSize];
        _bwtOutput = new byte[BurrowsWheelerInverse.MaxBlockSize];
        _bwt = new BurrowsWheelerInverse();
        _mtf = new MoveToFrontInverse();
        _rle1 = new Rle1Inverse();
    }

    /// <summary>Expected per-block CRC (32-bit) read from the block header.</summary>
    public uint ExpectedBlockCrc { get; private set; }

    /// <summary>
    /// Decode the next block from <paramref name="reader"/>. Caller must position the reader
    /// at the start of a block (after the stream's 4-byte 'BZh' header for the first block,
    /// or immediately after the previous block's terminator). Returns true if a block was
    /// decoded; false if the end-of-stream sentinel was found.
    /// </summary>
    public bool TryReadBlock(ref BZip2BitReader reader)
    {
        ulong magic = reader.ReadUInt48BigEndian();
        if (magic == EndOfStreamMagic)
            return false;
        if (magic != BlockMagic)
            throw new InvalidDataException($"Block magic mismatch: got 0x{magic:X12}, expected 0x{BlockMagic:X12}");

        ExpectedBlockCrc = reader.ReadUInt32BigEndian();
        uint randomized = reader.ReadBit();
        if (randomized != 0)
            throw new InvalidDataException("Randomized blocks not supported (deprecated since bzip2 0.9.5)");
        int origin = (int)reader.ReadBits(24);

        int alphabetUsed = ReadSymbolMap(ref reader);
        int alphabetSize = alphabetUsed + 2;  // RUNA + RUNB + ... + EOB

        int numTables = (int)reader.ReadBits(3);
        if (numTables < 2 || numTables > MaxTables)
            throw new InvalidDataException($"numTables {numTables} out of range");

        int numSelectors = (int)reader.ReadBits(15);
        if (numSelectors < 1 || numSelectors > MaxSelectors)
            throw new InvalidDataException($"numSelectors {numSelectors} out of range");

        ReadSelectors(ref reader, numSelectors, numTables);
        ReadHuffmanCodeLengths(ref reader, numTables, alphabetSize);
        for (int t = 0; t < numTables; t++)
            _decoders[t].Build(_codeLengths[t].AsSpan(0, alphabetSize));

        int eobSymbol = alphabetUsed + 1;

        // MTF list initialized to the alphabet (used bytes in ascending order).
        _mtf.Initialize(_alphabet, alphabetUsed);

        int bwtLen = DecodeSymbolStream(ref reader, numSelectors, eobSymbol);
        if (bwtLen <= 0 || bwtLen > BurrowsWheelerInverse.MaxBlockSize)
            throw new InvalidDataException($"Decoded BWT length {bwtLen} out of range");
        if ((uint)origin >= (uint)bwtLen)
            throw new InvalidDataException($"BWT origin {origin} >= block length {bwtLen}");

        _bwt.Decode(_bwtInput.AsSpan(0, bwtLen), bwtLen, origin, _bwtOutput.AsSpan(0, bwtLen));

        // RLE1 inverse converts the BWT output (which still contains 4-byte+count runs)
        // into the final byte stream. Bzip2 typically runs RLE1 inverse over the entire
        // BWT output at once, but we stream-drain via Rle1Inverse to keep buffers bounded.
        _rle1.Reset();
        _bwtOutputLength = bwtLen;
        _bwtOutputCursor = 0;
        return true;
    }

    /// <summary>
    /// Drain decompressed bytes into <paramref name="output"/>. Returns the number of bytes
    /// written. Zero indicates the current block is exhausted; caller should call
    /// <see cref="TryReadBlock"/> for the next block. Updates <paramref name="crc"/> with
    /// the CRC of the bytes written so the caller can verify against <see cref="ExpectedBlockCrc"/>
    /// at block end.
    /// </summary>
    public int Drain(Span<byte> output, ref uint crc)
    {
        if (output.IsEmpty || _bwtOutputCursor >= _bwtOutputLength && _rle1IsExhausted())
            return 0;

        int totalWritten = 0;
        while (output.Length > 0)
        {
            // Feed RLE1 input from the BWT output buffer; drain into caller's output span.
            ReadOnlySpan<byte> rleInput = _bwtOutput.AsSpan(_bwtOutputCursor, _bwtOutputLength - _bwtOutputCursor);
            var (consumed, written) = _rle1.Decode(rleInput, output);
            _bwtOutputCursor += consumed;
            crc = BZip2Crc32.Update(crc, output.Slice(0, written));
            output = output.Slice(written);
            totalWritten += written;

            if (written == 0)
            {
                // Either RLE1 has no pending output and no input, or output buffer was full.
                if (_bwtOutputCursor >= _bwtOutputLength && _rle1IsExhausted())
                    break;
                if (output.Length == 0) break;
                // Otherwise: should not happen — RLE1 with input should always make progress.
                break;
            }
        }
        return totalWritten;
    }

    private bool _rle1IsExhausted() => _bwtOutputCursor >= _bwtOutputLength;

    /// <summary>
    /// Read the bzip2 symbol map: 16-bit group bitmap, then per-set-group a 16-bit byte
    /// bitmap. The bytes that appear in the block, in ascending byte order, populate
    /// <see cref="_alphabet"/>. Returns the number of distinct bytes used.
    /// </summary>
    private int ReadSymbolMap(ref BZip2BitReader reader)
    {
        uint groupBits = reader.ReadBits(16);
        int used = 0;
        for (int i = 0; i < 16; i++)
        {
            if ((groupBits & (1u << (15 - i))) == 0) continue;
            uint byteBits = reader.ReadBits(16);
            for (int j = 0; j < 16; j++)
            {
                if ((byteBits & (1u << (15 - j))) != 0)
                    _alphabet[used++] = (byte)(i * 16 + j);
            }
        }
        if (used == 0)
            throw new InvalidDataException("Empty alphabet — block has no usable symbols");
        return used;
    }

    /// <summary>
    /// Read selectors: each is a unary-encoded MTF position into the table list. After
    /// reading, MTF-decode the sequence into raw table indices.
    /// </summary>
    private void ReadSelectors(ref BZip2BitReader reader, int numSelectors, int numTables)
    {
        Span<byte> tablePerm = stackalloc byte[MaxTables];
        for (int i = 0; i < numTables; i++) tablePerm[i] = (byte)i;

        for (int s = 0; s < numSelectors; s++)
        {
            int j = 0;
            while (reader.ReadBit() == 1)
            {
                j++;
                if (j >= numTables)
                    throw new InvalidDataException("Selector unary code exceeds numTables");
            }
            // Move element at position j to front of tablePerm.
            byte selected = tablePerm[j];
            for (int k = j; k > 0; k--) tablePerm[k] = tablePerm[k - 1];
            tablePerm[0] = selected;
            _selectors[s] = selected;
        }
    }

    /// <summary>
    /// Read per-table per-symbol code lengths: 5-bit start, then for each symbol read
    /// delta bits. Each delta: while next bit is 1, read another bit (0 → +1, 1 → -1);
    /// terminating 0 bit ends the delta loop and records the current length for that symbol.
    /// </summary>
    private void ReadHuffmanCodeLengths(ref BZip2BitReader reader, int numTables, int alphabetSize)
    {
        for (int t = 0; t < numTables; t++)
        {
            int curr = (int)reader.ReadBits(5);
            byte[] lengths = _codeLengths[t];
            for (int s = 0; s < alphabetSize; s++)
            {
                while (reader.ReadBit() == 1)
                {
                    if (reader.ReadBit() == 0) curr++;
                    else curr--;
                    if (curr < 1 || curr > HuffmanDecoder.MaxCodeLength)
                        throw new InvalidDataException($"Code length {curr} out of range");
                }
                lengths[s] = (byte)curr;
            }
            // Zero out unused alphabet slots so HuffmanDecoder.Build sees them as absent.
            for (int s = alphabetSize; s < HuffmanDecoder.MaxAlphabetSize; s++)
                lengths[s] = 0;
        }
    }

    /// <summary>
    /// The main symbol-decoding loop: read Huffman symbols using the active table (selected
    /// every 50 symbols), expand RUNA/RUNB into MTF-index-zero runs, route other symbols
    /// through the MTF inverse, and write the resulting bytes into the BWT input buffer.
    /// Stops at EOB. Returns the BWT input length.
    /// </summary>
    private int DecodeSymbolStream(ref BZip2BitReader reader, int numSelectors, int eobSymbol)
    {
        int bwtPos = 0;
        int selectorIdx = 0;
        int symbolsInGroup = 0;
        HuffmanDecoder activeTable = _decoders[_selectors[0]];

        while (true)
        {
            if (symbolsInGroup == GroupSize)
            {
                symbolsInGroup = 0;
                selectorIdx++;
                if (selectorIdx >= numSelectors)
                    throw new InvalidDataException("Selector underflow — symbol stream exceeded selector budget");
                activeTable = _decoders[_selectors[selectorIdx]];
            }
            symbolsInGroup++;

            int sym = activeTable.DecodeSymbol(ref reader);
            if (sym == eobSymbol)
                return bwtPos;

            if (sym == RUNA || sym == RUNB)
            {
                // Accumulate run-length using bzip2's bijective base-2 encoding.
                int runLength = -1;
                int unit = 1;
                do
                {
                    runLength += sym == RUNA ? unit : 2 * unit;
                    unit <<= 1;
                    if (symbolsInGroup == GroupSize)
                    {
                        symbolsInGroup = 0;
                        selectorIdx++;
                        if (selectorIdx >= numSelectors)
                            throw new InvalidDataException("Selector underflow during run");
                        activeTable = _decoders[_selectors[selectorIdx]];
                    }
                    symbolsInGroup++;
                    sym = activeTable.DecodeSymbol(ref reader);
                } while (sym == RUNA || sym == RUNB);
                runLength++;
                if (bwtPos + runLength > BurrowsWheelerInverse.MaxBlockSize)
                    throw new InvalidDataException("BWT input overflow during RUNA/RUNB run");
                byte runByte = _mtf.Decode(0);
                for (int k = 0; k < runLength; k++) _bwtInput[bwtPos++] = runByte;

                if (sym == eobSymbol)
                    return bwtPos;
            }

            // Non-RUN symbol: MTF index = sym - 1.
            int mtfIndex = sym - 1;
            if ((uint)mtfIndex >= 256)
                throw new InvalidDataException($"Invalid MTF index {mtfIndex} from symbol {sym}");
            if (bwtPos >= BurrowsWheelerInverse.MaxBlockSize)
                throw new InvalidDataException("BWT input overflow on regular symbol");
            _bwtInput[bwtPos++] = _mtf.Decode(mtfIndex);
        }
    }
}
