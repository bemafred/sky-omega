using System;
using System.IO;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Multi-index temporal triple store for Sky Omega
/// Supports multiple temporal query patterns with optimal index selection
/// 
/// Indexes:
/// 1. SPOT: Subject-Predicate-Object-Time (primary)
/// 2. POST: Predicate-Object-Subject-Time (predicate queries)
/// 3. OSPT: Object-Subject-Predicate-Time (object queries)
/// 4. TSPO: Time-Subject-Predicate-Object (temporal range scans)
/// </summary>
public sealed class MultiTemporalStore : IDisposable
{
    private readonly TemporalTripleStore _spotIndex; // Primary index
    private readonly TemporalTripleStore _postIndex; // Predicate-first
    private readonly TemporalTripleStore _osptIndex; // Object-first
    private readonly TemporalTripleStore _tspoIndex; // Time-first

    private readonly AtomStore _atoms;

    public MultiTemporalStore(string baseDirectory)
    {
        if (!Directory.Exists(baseDirectory))
            Directory.CreateDirectory(baseDirectory);

        var spotPath = Path.Combine(baseDirectory, "spot.tdb");
        var postPath = Path.Combine(baseDirectory, "post.tdb");
        var osptPath = Path.Combine(baseDirectory, "ospt.tdb");
        var tspoPath = Path.Combine(baseDirectory, "tspo.tdb");
        var atomPath = Path.Combine(baseDirectory, "atoms");

        // Create shared atom store for all indexes
        _atoms = new AtomStore(atomPath);

        // Create temporal stores with shared atom store
        _spotIndex = new TemporalTripleStore(spotPath, _atoms);
        _postIndex = new TemporalTripleStore(postPath, _atoms);
        _osptIndex = new TemporalTripleStore(osptPath, _atoms);
        _tspoIndex = new TemporalTripleStore(tspoPath, _atoms);
    }

    /// <summary>
    /// Add a temporal triple to all indexes
    /// </summary>
    public void Add(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo)
    {
        _spotIndex.AddHistorical(subject, predicate, obj, validFrom, validTo);
        _postIndex.AddHistorical(predicate, obj, subject, validFrom, validTo);
        _osptIndex.AddHistorical(obj, subject, predicate, validFrom, validTo);
        _tspoIndex.AddHistorical(subject, predicate, obj, validFrom, validTo);
    }

    /// <summary>
    /// Add a current fact (valid from now onwards)
    /// </summary>
    public void AddCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj)
    {
        _spotIndex.AddCurrent(subject, predicate, obj);
        _postIndex.AddCurrent(predicate, obj, subject);
        _osptIndex.AddCurrent(obj, subject, predicate);
        _tspoIndex.AddCurrent(subject, predicate, obj);
    }

    /// <summary>
    /// Query with optimal index selection
    /// </summary>
    public TemporalResultEnumerator Query(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        TemporalQueryType queryType,
        DateTimeOffset? asOfTime = null,
        DateTimeOffset? rangeStart = null,
        DateTimeOffset? rangeEnd = null)
    {
        // Select optimal index
        var (selectedIndex, indexType) = SelectOptimalIndex(subject, predicate, obj, queryType);
        
        TemporalTripleStore.TemporalTripleEnumerator enumerator;
        
        if (queryType == TemporalQueryType.AsOf)
        {
            enumerator = selectedIndex.QueryAsOf(
                subject, predicate, obj,
                asOfTime ?? DateTimeOffset.UtcNow);
        }
        else if (queryType == TemporalQueryType.Range)
        {
            enumerator = selectedIndex.QueryRange(
                subject, predicate, obj,
                rangeStart ?? DateTimeOffset.MinValue,
                rangeEnd ?? DateTimeOffset.MaxValue);
        }
        else
        {
            enumerator = selectedIndex.QueryHistory(subject, predicate, obj);
        }
        
        return new TemporalResultEnumerator(enumerator, indexType, _atoms);
    }

    /// <summary>
    /// Query current state (as of now)
    /// </summary>
    public TemporalResultEnumerator QueryCurrent(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj)
    {
        return Query(subject, predicate, obj, TemporalQueryType.AsOf);
    }

    /// <summary>
    /// Query as of a specific point in time (time-travel query)
    /// </summary>
    public TemporalResultEnumerator QueryAsOf(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset asOfTime)
    {
        return Query(subject, predicate, obj, TemporalQueryType.AsOf, asOfTime: asOfTime);
    }

    /// <summary>
    /// Query all versions (evolution over time)
    /// </summary>
    public TemporalResultEnumerator QueryEvolution(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj)
    {
        return Query(subject, predicate, obj, TemporalQueryType.AllTime);
    }

    /// <summary>
    /// Time-travel query: What was true at specific time?
    /// </summary>
    public TemporalResultEnumerator TimeTravelTo(
        DateTimeOffset targetTime,
        ReadOnlySpan<char> subject = default,
        ReadOnlySpan<char> predicate = default,
        ReadOnlySpan<char> obj = default)
    {
        return Query(subject, predicate, obj, TemporalQueryType.AsOf, asOfTime: targetTime);
    }

    /// <summary>
    /// Temporal range query: What changed during period?
    /// </summary>
    public TemporalResultEnumerator QueryChanges(
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        ReadOnlySpan<char> subject = default,
        ReadOnlySpan<char> predicate = default,
        ReadOnlySpan<char> obj = default)
    {
        return Query(
            subject, predicate, obj,
            TemporalQueryType.Range,
            rangeStart: periodStart,
            rangeEnd: periodEnd);
    }

    private (TemporalTripleStore Index, TemporalIndexType Type) SelectOptimalIndex(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        TemporalQueryType queryType)
    {
        var subjectBound = !subject.IsEmpty && subject[0] != '?';
        var predicateBound = !predicate.IsEmpty && predicate[0] != '?';
        var objectBound = !obj.IsEmpty && obj[0] != '?';
        
        // For time-range queries, prefer TSPO index
        if (queryType == TemporalQueryType.Range)
        {
            return (_tspoIndex, TemporalIndexType.TSPO);
        }
        
        // Otherwise select based on bound variables
        if (subjectBound)
        {
            return (_spotIndex, TemporalIndexType.SPOT);
        }
        else if (predicateBound)
        {
            return (_postIndex, TemporalIndexType.POST);
        }
        else if (objectBound)
        {
            return (_osptIndex, TemporalIndexType.OSPT);
        }
        else
        {
            return (_spotIndex, TemporalIndexType.SPOT);
        }
    }

    public void Dispose()
    {
        _spotIndex?.Dispose();
        _postIndex?.Dispose();
        _osptIndex?.Dispose();
        _tspoIndex?.Dispose();
        _atoms?.Dispose();
    }

    /// <summary>
    /// Get storage statistics
    /// </summary>
    public (long TripleCount, long AtomCount, long TotalBytes) GetStatistics()
    {
        var tripleCount = _spotIndex.TripleCount;
        var (atomCount, totalBytes, _) = _atoms.GetStatistics();
        return (tripleCount, atomCount, totalBytes);
    }
}

/// <summary>
/// Enumerator that remaps results from different temporal indexes
/// </summary>
public ref struct TemporalResultEnumerator
{
    private TemporalTripleStore.TemporalTripleEnumerator _baseEnumerator;
    private readonly TemporalIndexType _indexType;
    private readonly AtomStore _atoms;

    internal TemporalResultEnumerator(
        TemporalTripleStore.TemporalTripleEnumerator baseEnumerator,
        TemporalIndexType indexType,
        AtomStore atoms)
    {
        _baseEnumerator = baseEnumerator;
        _indexType = indexType;
        _atoms = atoms;
    }

    public bool MoveNext() => _baseEnumerator.MoveNext();

    public readonly ResolvedTemporalTriple Current
    {
        get
        {
            var triple = _baseEnumerator.Current;

            // Remap based on index type
            long s, p, o;

            switch (_indexType)
            {
                case TemporalIndexType.SPOT:
                    s = triple.SubjectAtom;
                    p = triple.PredicateAtom;
                    o = triple.ObjectAtom;
                    break;

                case TemporalIndexType.POST:
                    p = triple.SubjectAtom;
                    o = triple.PredicateAtom;
                    s = triple.ObjectAtom;
                    break;

                case TemporalIndexType.OSPT:
                    o = triple.SubjectAtom;
                    s = triple.PredicateAtom;
                    p = triple.ObjectAtom;
                    break;

                case TemporalIndexType.TSPO:
                    s = triple.SubjectAtom;
                    p = triple.PredicateAtom;
                    o = triple.ObjectAtom;
                    break;

                default:
                    s = p = o = 0;
                    break;
            }
            
            // Clamp milliseconds to valid DateTimeOffset range
            const long MaxValidMs = 253402300799999L; // Dec 31, 9999

            return new ResolvedTemporalTriple(
                _atoms.GetAtomString(s),
                _atoms.GetAtomString(p),
                _atoms.GetAtomString(o),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(triple.ValidFrom, MaxValidMs)),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(triple.ValidTo, MaxValidMs)),
                DateTimeOffset.FromUnixTimeMilliseconds(Math.Min(triple.TransactionTime, MaxValidMs))
            );
        }
    }

    public TemporalResultEnumerator GetEnumerator() => this;
}

public enum TemporalIndexType
{
    SPOT, // Subject-Predicate-Object-Time
    POST, // Predicate-Object-Subject-Time
    OSPT, // Object-Subject-Predicate-Time
    TSPO  // Time-Subject-Predicate-Object
}

/// <summary>
/// Resolved temporal triple with time dimensions
/// </summary>
public readonly ref struct ResolvedTemporalTriple
{
    public readonly ReadOnlySpan<char> Subject;
    public readonly ReadOnlySpan<char> Predicate;
    public readonly ReadOnlySpan<char> Object;
    public readonly DateTimeOffset ValidFrom;
    public readonly DateTimeOffset ValidTo;
    public readonly DateTimeOffset TransactionTime;

    public ResolvedTemporalTriple(
        ReadOnlySpan<char> subject,
        ReadOnlySpan<char> predicate,
        ReadOnlySpan<char> obj,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        DateTimeOffset transactionTime)
    {
        Subject = subject;
        Predicate = predicate;
        Object = obj;
        ValidFrom = validFrom;
        ValidTo = validTo;
        TransactionTime = transactionTime;
    }
}
