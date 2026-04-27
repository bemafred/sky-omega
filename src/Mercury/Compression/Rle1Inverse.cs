using System;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Compression;

/// <summary>
/// Inverse of bzip2's pre-BWT byte-level run-length encoding (RLE1). ADR-036 Decision 2.
/// Applied as the FINAL stage of decompression — input is BWT-inverted bytes, output is
/// the original uncompressed stream.
/// </summary>
/// <remarks>
/// <para>
/// RLE1 contracts runs of 4+ identical bytes during compression: emit 4 copies of the
/// byte verbatim, then a single "extra count" byte (0–255) for the additional copies
/// beyond 4. The inverse runs a small state machine: when the input emits the 4th
/// consecutive identical byte, the next input byte is the extra count, and the
/// inverse emits that many additional copies before resuming the streaming scan.
/// </para>
/// <para>
/// State is held in this class instance; the same instance is reused across calls within
/// one block decode (post-BWT inverse pumps bytes through). State persists across calls
/// because RLE1 runs may straddle the chunk boundary the caller chooses to drain in.
/// </para>
/// </remarks>
internal sealed class Rle1Inverse
{
    private int _prevByte = -1;
    private int _runCount = 0;
    /// <summary>Pending repeats from a finished run; emitted before consuming new input.</summary>
    private byte _pendingByte;
    private int _pendingRepeats;

    public void Reset()
    {
        _prevByte = -1;
        _runCount = 0;
        _pendingRepeats = 0;
    }

    /// <summary>
    /// Drain bytes through the RLE1 inverse. Reads from <paramref name="input"/> and writes
    /// to <paramref name="output"/>. Returns the number of input bytes consumed and output
    /// bytes written. Callers loop on this method until input is exhausted; if output runs
    /// short before input, pending repeats are stored on the instance and consumed first
    /// on the next call.
    /// </summary>
    public (int Consumed, int Written) Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int outIdx = 0;

        // Drain any repeats left over from a previous call.
        while (_pendingRepeats > 0 && outIdx < output.Length)
        {
            output[outIdx++] = _pendingByte;
            _pendingRepeats--;
        }
        if (outIdx == output.Length)
            return (0, outIdx);

        int inIdx = 0;
        while (inIdx < input.Length)
        {
            // The extra-count byte is consumed unconditionally — it does not produce
            // its own output byte, so the output-buffer guard can't gate its consumption.
            // Overflow output is captured in pending state and drained on the next call.
            if (_runCount == 4)
            {
                int extra = input[inIdx];
                _runCount = 0;
                inIdx++;

                int writable = Math.Min(extra, output.Length - outIdx);
                for (int i = 0; i < writable; i++) output[outIdx + i] = (byte)_prevByte;
                outIdx += writable;
                if (writable < extra)
                {
                    _pendingByte = (byte)_prevByte;
                    _pendingRepeats = extra - writable;
                    _prevByte = -1;
                    return (inIdx, outIdx);
                }
                _prevByte = -1;
                continue;
            }

            // Body of the loop produces one output byte per iteration; if there's no
            // room, stop here and let the caller drain.
            if (outIdx == output.Length) break;

            byte b = input[inIdx];
            if (b == _prevByte)
            {
                _runCount++;
            }
            else
            {
                _prevByte = b;
                _runCount = 1;
            }

            output[outIdx++] = b;
            inIdx++;
        }

        return (inIdx, outIdx);
    }
}
