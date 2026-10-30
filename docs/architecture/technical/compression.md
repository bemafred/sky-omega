# Compression: bzip2 decompression

Mercury reads bzip2-compressed RDF (`.ttl.bz2`, `.nt.bz2` and the other formats) through its own decompressor, written
against the .NET base class library only ([ADR-036](../../adrs/mercury/ADR-036-bzip2-streaming-decompression.md)).
Wikidata publishes its dumps as bzip2, and reading them directly avoids the 3.1 TB uncompressed copy of
`latest-all` that the first 21.3 B load needed. Mercury decompresses; it does not write bzip2.

The code is in `src/Mercury/Compression/` (2,255 lines, namespace `SkyOmega.Mercury.Compression`), the tests in
`tests/Mercury.Tests/Compression/`.

## Public API

Two public types, both read-only, forward-only `Stream`s:

| Type | Constructor | Reads |
|------|-------------|-------|
| `BZip2DecompressorStream` | `(Stream compressed, bool leaveOpen = false)` | One bzip2 stream, or several concatenated, on the calling thread |
| `ParallelBZip2DecompressorStream` | `(Stream compressed, int workerCount = -1, bool leaveOpen = false)` | One bzip2 stream, its blocks decoded on a pool of worker threads. `-1` means `Math.Max(1, Environment.ProcessorCount * 4 / 5)`, 14 workers on 18 cores |

Both:

- `CanRead` is true until the stream is disposed; `CanSeek` and `CanWrite` are false.
- `Length`, `Position`, `Seek`, `SetLength` and `Write` throw `NotSupportedException`.
- `Read` after `Dispose` throws `ObjectDisposedException`.
- `Read` returns 0 at the end of the input, after the last stream's combined CRC has been checked.
- `ReadAsync` is not overridden; the base `Stream` implementation runs `Read`.

Errors:

| Input | `BZip2DecompressorStream` | `ParallelBZip2DecompressorStream` |
|-------|---------------------------|-----------------------------------|
| A block or combined CRC that does not match | `InvalidDataException` | `InvalidDataException` |
| A bad header, block magic, table or symbol | `InvalidDataException` | `InvalidDataException` |
| A randomized block (deprecated since bzip2 0.9.5) | `InvalidDataException` | `InvalidDataException` |
| Truncated input, anywhere from the header to the last CRC byte | `EndOfStreamException` | `InvalidDataException` ("Truncated bzip2 stream: …") |
| Two or more concatenated streams | read in order | `InvalidDataException` at the end of the first stream |

The parallel stream returned the first stream's bytes alone for a concatenated input until 2026-10-30, despite
documenting that it rejected it; it now throws.

## Where Mercury uses it

`RdfEngine.WrapWithDecompression` wraps a source in `BZip2DecompressorStream` when its compression type is bzip2.
The format is detected from the extension before `.bz2` (`RdfFormat`). Loading (`mercury --bulk-load`, `LOAD`) and
`mercury --convert` both read through it.

No Mercury code path uses `ParallelBZip2DecompressorStream`. ADR-036 recommends it for `--convert`, where the parser
does not limit throughput, but `--convert` reads through `RdfEngine` like loading does.

## The format

A bzip2 stream is:

1. `BZh` and a level digit `1`–`9`, the block size in units of 100 KB. Mercury checks the digit and otherwise does not
   use it.
2. Blocks, each starting with the 48-bit magic `0x314159265359` (the digits of π). Blocks are aligned to bits, not
   bytes.
3. The 48-bit end-of-stream magic `0x177245385090` (√π), the 32-bit combined CRC, and padding to a whole byte.

A file may hold several streams one after another, as `cat a.bz2 b.bz2` makes.

A block (`BZip2BlockReader`) holds, in order:

1. the 32-bit CRC of the block's decompressed bytes;
2. a 1-bit randomized flag, which must be 0;
3. the 24-bit origin pointer of the Burrows–Wheeler transform;
4. the symbol map: 16 bits marking which groups of 16 byte values occur, then 16 bits per marked group;
5. the number of Huffman tables (3 bits, 2–6) and of selectors (15 bits);
6. the selectors, each a move-to-front index written in unary;
7. for each table, a 5-bit start length and the code lengths as deltas;
8. the Huffman-coded symbols, up to the end-of-block symbol.

## Decoding a block

| Stage | Type | Lines | What it does |
|-------|------|------:|--------------|
| Bits | `BZip2BitReader` | 189 | Reads most-significant bit first through a 64-bit accumulator. A `ref struct`, so it lives on the stack |
| Huffman | `HuffmanDecoder` | 232 | Canonical codes of up to 20 bits over up to 258 symbols (the bytes in use, `RUNA`, `RUNB`, end-of-block). A 2,048-entry primary table indexed by the next 11 bits, with a secondary table for longer codes. The selectors switch tables every 50 symbols |
| Zero runs and move-to-front | `BZip2BlockReader`, `MoveToFrontInverse` | 317, 99 | `RUNA` and `RUNB` spell a run length of the front byte in bijective base 2; every other symbol is a move-to-front index into a 256-byte list |
| Burrows–Wheeler | `BurrowsWheelerInverse` | 143 | Builds the index array `T` from the transformed block and walks it from the origin pointer |
| Byte runs | `Rle1Inverse` | 115 | After four equal bytes, the next byte is how many more copies follow (0–255) |
| CRC | `BZip2Crc32` | 100 | See below |
| Streams | `BZip2DecompressorStream` | 258 | Header, blocks and trailer as a state machine; concatenated streams |
| Parallel | `BZip2BlockBoundaryScanner`, `ParallelBZip2DecompressorStream` | 381, 421 | See below |

The block's output is checked against its CRC as it is drained.

## CRC

bzip2's CRC is CRC-32 with the polynomial `0x04C11DB7`, **not reflected**: bits are taken most-significant first. It
starts at `0xFFFFFFFF` and is inverted at the end. .NET's `System.IO.Hashing.Crc32` is the reflected variant used by
gzip and zlib and gives different values, so `BZip2Crc32` is its own type: slicing-by-8, with 8 KB of tables.

Each block's CRC is checked, and so is the stream's combined CRC: for each block in order,
`combined = rotate_left(combined, 1) ^ blockCrc`, starting from 0 in each stream. Neither check can be turned off.

## Memory

`BZip2DecompressorStream` holds a 2 MB window of compressed input, refilled when less than 1 MB remains (a block is
at most about 900 KB compressed), and one block reader. The block reader's Burrows–Wheeler index array is 900,000
`int`s, 3.6 MB, for the largest block. Every buffer is allocated when the stream is constructed or first read; after
the first read, reading allocates nothing (`Read_AfterTheFirstBlock_DoesNotAllocate`, 0 bytes over the 3 MB fixture).

`ParallelBZip2DecompressorStream` gives each worker its own block reader, rents output buffers from
`ArrayPool<byte>.Shared`, and scans through an 8 MB window.

## Parallel decoding

Each block depends only on itself, so blocks can be decoded independently:

1. One producer runs `BZip2BlockBoundaryScanner`, which finds each block's bit range by looking for the block magic
   one bit at a time, without decoding.
2. Each block's bytes go through a bounded channel to the workers. A worker decodes with its own `BZip2BlockReader`
   and checks the block's CRC.
3. Results wait in a priority queue keyed by block number. The reading thread takes them in order and computes the
   combined CRC.

ADR-036 measured a 2.62× speedup ceiling on a 50 MB, 58-block fixture, limited by the scanner (Phase 2, 2026-04-30).

Two limits:

- **One stream only.** A concatenated file is rejected (see Errors).
- **A chance block magic is not handled.** The scanner takes every occurrence of the 48-bit block magic as the start
  of a block. bzip2 does not prevent the pattern from occurring inside compressed data, and one that did would cut a
  block in two and make a valid file fail to decode. At 2⁻⁴⁸ per bit position, about 0.004 such occurrences are
  expected in the 122 GB `latest-all.ttl.bz2`. None has been observed. The single-threaded stream is not affected: it
  finds each block's end by decoding it.

## Validation

- **Unit tests** for each stage: `BZip2BitReaderTests`, `HuffmanDecoderTests`, `MoveToFrontInverseTests`,
  `Rle1InverseTests`, `BurrowsWheelerInverseTests`, `BZip2Crc32Tests`, `BZip2BlockReaderTests`,
  `MultiBlockReuseHypothesisTests`.
- **Stream tests** (`BZip2DecompressorStreamTests`, `ParallelBZip2DecompressorStreamTests`): the fixtures round-trip;
  one-byte and random-size reads; a corrupted CRC throws; seeking and `Position` are not supported; a disposed stream
  throws; an empty stream reads as nothing; concatenated streams read in order (single-threaded) or are rejected
  (parallel); truncation at five points, from the header to the last CRC byte, throws; the parallel stream's output
  equals the single-threaded stream's for 1 to 14 workers; reading allocates nothing after the first read.
- **Against the reference decompressor**, 2026-10-30: the first 4 GiB (4,294,967,296 bytes) of
  `latest-all.ttl.bz2`, decompressed by bzip2 1.0.8, by `BZip2DecompressorStream` and by
  `ParallelBZip2DecompressorStream`, have the same SHA-256
  (`c934a1e463306ec5ac089f8d86180d3b2070f4b6132d6e4fd9b465288c30b9c3`).
- **In production**, every 21.3 B load since cycle 8 (2026-05-03) has read the whole of `latest-all.ttl.bz2`
  through `BZip2DecompressorStream`, with every block's CRC and the combined CRC checked.

## Test fixtures

The fixtures are committed in `tests/Mercury.Tests/Compression/Fixtures/` and copied to the test output. No test
runs `bzip2`: the first multi-block tests started it with `Process.Start`, and their failures were subprocess I/O
hangs, not decoding errors (`a11f873`).

| Fixture | Source | Compressed | Content |
|---------|-------:|-----------:|---------|
| `hello.txt.bz2` | 11 B | 50 B | `Hello world`, no newline (`hello.txt`) |
| `pangram.txt.bz2` | 45 B | 81 B | `The quick brown fox jumps over the lazy dog.` and a trailing space (`pangram.txt`) |
| `repeat.txt.bz2` | 800 B | 48 B | `AAAABBBB` 100 times (`repeat.txt`): runs of exactly four, the run-length stages' edge |
| `multiblock.txt.bz2` | 3,145,801 B | 410,498 B | Synthetic N-Triples in four blocks at level 9. Its source and the script that made it were not committed |

To regenerate a small fixture, run bzip2 1.0.8 at level 9 on its source:

```bash
cd tests/Mercury.Tests/Compression/Fixtures
bzip2 -9 -c hello.txt > hello.txt.bz2     # likewise pangram.txt and repeat.txt
```

This reproduces the committed files byte for byte (checked 2026-10-30). Replacing `multiblock.txt.bz2` takes a
new multi-block file (more than 900 KB of input at level 9) and new values for its sizes and truncation points in the
tests. The empty stream is written out in its test: the 14 bytes of `bzip2 -9 -c < /dev/null`.

Three measurement classes (`LargeFixtureMeasurement`, `ConvertPathThroughputMeasurement`,
`ParallelBZip2ContentionTrace`) read `/tmp/multiblock-large.txt.bz2`, made by `bzip2 -9` from a 50 MB text file and
not committed. Without it they return without measuring and report as passed. `ParallelBZip2ThroughputMeasurement`
uses the committed multi-block fixture.
