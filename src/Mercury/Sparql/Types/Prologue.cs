namespace SkyOmega.Mercury.Sparql.Types;

internal struct Prologue
{
    public const int MaxPrefixes = 32;
    private int _prefixCount;

    // Store prefixes as start/length pairs into source string
    // Each prefix entry: (prefixStart, prefixLength, iriStart, iriLength)
    private int _p0s, _p0l, _i0s, _i0l;
    private int _p1s, _p1l, _i1s, _i1l;
    private int _p2s, _p2l, _i2s, _i2l;
    private int _p3s, _p3l, _i3s, _i3l;
    private int _p4s, _p4l, _i4s, _i4l;
    private int _p5s, _p5l, _i5s, _i5l;
    private int _p6s, _p6l, _i6s, _i6l;
    private int _p7s, _p7l, _i7s, _i7l;
    private int _p8s, _p8l, _i8s, _i8l;
    private int _p9s, _p9l, _i9s, _i9l;
    private int _p10s, _p10l, _i10s, _i10l;
    private int _p11s, _p11l, _i11s, _i11l;
    private int _p12s, _p12l, _i12s, _i12l;
    private int _p13s, _p13l, _i13s, _i13l;
    private int _p14s, _p14l, _i14s, _i14l;
    private int _p15s, _p15l, _i15s, _i15l;

    public int BaseUriStart;   // Start offset in source span
    public int BaseUriLength;  // Length in source span

    public readonly int PrefixCount => _prefixCount;

    public void AddPrefix(ReadOnlySpan<char> prefix, ReadOnlySpan<char> iri)
    {
        // This overload doesn't work well - we need start/length pairs
        // For now, just count - this will be fixed by AddPrefixRange
        _prefixCount++;
    }

    public void AddPrefixRange(int prefixStart, int prefixLength, int iriStart, int iriLength)
    {
        if (_prefixCount >= MaxPrefixes)
            throw new SparqlParseException("Too many prefix declarations (max 32)");

        switch (_prefixCount)
        {
            case 0: _p0s = prefixStart; _p0l = prefixLength; _i0s = iriStart; _i0l = iriLength; break;
            case 1: _p1s = prefixStart; _p1l = prefixLength; _i1s = iriStart; _i1l = iriLength; break;
            case 2: _p2s = prefixStart; _p2l = prefixLength; _i2s = iriStart; _i2l = iriLength; break;
            case 3: _p3s = prefixStart; _p3l = prefixLength; _i3s = iriStart; _i3l = iriLength; break;
            case 4: _p4s = prefixStart; _p4l = prefixLength; _i4s = iriStart; _i4l = iriLength; break;
            case 5: _p5s = prefixStart; _p5l = prefixLength; _i5s = iriStart; _i5l = iriLength; break;
            case 6: _p6s = prefixStart; _p6l = prefixLength; _i6s = iriStart; _i6l = iriLength; break;
            case 7: _p7s = prefixStart; _p7l = prefixLength; _i7s = iriStart; _i7l = iriLength; break;
            case 8: _p8s = prefixStart; _p8l = prefixLength; _i8s = iriStart; _i8l = iriLength; break;
            case 9: _p9s = prefixStart; _p9l = prefixLength; _i9s = iriStart; _i9l = iriLength; break;
            case 10: _p10s = prefixStart; _p10l = prefixLength; _i10s = iriStart; _i10l = iriLength; break;
            case 11: _p11s = prefixStart; _p11l = prefixLength; _i11s = iriStart; _i11l = iriLength; break;
            case 12: _p12s = prefixStart; _p12l = prefixLength; _i12s = iriStart; _i12l = iriLength; break;
            case 13: _p13s = prefixStart; _p13l = prefixLength; _i13s = iriStart; _i13l = iriLength; break;
            case 14: _p14s = prefixStart; _p14l = prefixLength; _i14s = iriStart; _i14l = iriLength; break;
            case 15: _p15s = prefixStart; _p15l = prefixLength; _i15s = iriStart; _i15l = iriLength; break;
            default: throw new SparqlParseException("Too many prefix declarations (max 16)");
        }
        _prefixCount++;
    }

    public readonly (int PrefixStart, int PrefixLength, int IriStart, int IriLength) GetPrefix(int index)
    {
        return index switch
        {
            0 => (_p0s, _p0l, _i0s, _i0l),
            1 => (_p1s, _p1l, _i1s, _i1l),
            2 => (_p2s, _p2l, _i2s, _i2l),
            3 => (_p3s, _p3l, _i3s, _i3l),
            4 => (_p4s, _p4l, _i4s, _i4l),
            5 => (_p5s, _p5l, _i5s, _i5l),
            6 => (_p6s, _p6l, _i6s, _i6l),
            7 => (_p7s, _p7l, _i7s, _i7l),
            8 => (_p8s, _p8l, _i8s, _i8l),
            9 => (_p9s, _p9l, _i9s, _i9l),
            10 => (_p10s, _p10l, _i10s, _i10l),
            11 => (_p11s, _p11l, _i11s, _i11l),
            12 => (_p12s, _p12l, _i12s, _i12l),
            13 => (_p13s, _p13l, _i13s, _i13l),
            14 => (_p14s, _p14l, _i14s, _i14l),
            15 => (_p15s, _p15l, _i15s, _i15l),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}
