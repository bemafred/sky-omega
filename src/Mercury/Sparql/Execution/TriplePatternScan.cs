using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;

namespace SkyOmega.Mercury.Sparql.Execution;

/// <summary>
/// Scans a single triple pattern against the QuadStore.
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// </summary>
internal ref struct TriplePatternScan
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly TriplePattern _pattern;
    private TemporalResultEnumerator _enumerator;
    private bool _initialized;
    private readonly BindingTable _initialBindings;
    private readonly int _initialBindingsCount;
    private readonly ReadOnlySpan<char> _graph;

    // Temporal query parameters
    private readonly TemporalQueryMode _temporalMode;
    private readonly DateTimeOffset _asOfTime;
    private readonly DateTimeOffset _rangeStart;
    private readonly DateTimeOffset _rangeEnd;

    // For property path traversal
    private readonly bool _isInverse;
    private readonly bool _isZeroOrMore;
    private readonly bool _isOneOrMore;
    private readonly bool _isZeroOrOne;
    private readonly bool _isAlternative;
    private readonly bool _isNegatedSet;
    private readonly bool _isGroupedZeroOrMore;
    private readonly bool _isGroupedOneOrMore;
    private readonly bool _isGroupedZeroOrOne;
    private readonly bool _isInverseGroup;

    // State for transitive path traversal
    private HashSet<string>? _visited;
    private Queue<string>? _frontier;
    private string? _currentNode;
    private string? _startNode;  // Original start node for binding
    private bool _emittedReflexive;

    // State for multi-start transitive path (when both subject and object are unbound)
    private Queue<string>? _startingNodes;  // All potential starting nodes to process
    private HashSet<string>? _allNodes;     // All nodes seen (for reflexive emissions)
    private int _reflexiveIndex;            // Index for iterating through reflexive emissions
    private bool _inReflexivePhase;         // True when emitting reflexive bindings
    private List<string>? _reflexiveList;   // List of all nodes for reflexive iteration

    // State for alternative path traversal (p1|p2|p3|...)
    // Supports n-ary alternatives by tracking remaining span to process
    private int _alternativePhase; // Current phase (0 = first segment, 1+ = subsequent segments)
    private int _alternativeRemainingStart;  // Start of remaining right span
    private int _alternativeRemainingLength; // Length of remaining right span

    // State for sequence within alternative (e.g., p2/:p3 in p1|p2/:p3|p4)
    private bool _inSequencePhase;           // Currently collecting intermediates from first step
    private bool _inSequenceSecondStep;      // Currently executing second step (intermediates → final)
    private bool _sequenceFirstStepIsInverse; // True if first step of sequence is inverse (^pred)
    private Queue<string>? _sequenceIntermediates; // Intermediate nodes from first step of sequence
    // Note: Second predicate stored in _expandedPredicate as string

    // State for negated property set with inverse predicates
    // 0 = direct scan (check direct predicates), 1 = inverse scan (check inverse predicates, swap bindings)
    private int _negatedSetPhase;
    private bool _negatedSetHasInverse;  // True if set contains inverse predicates
    private bool _negatedSetHasDirect;   // True if set contains direct predicates

    // State for grouped sequence path traversal ((p1/p2/p3)*)
    private Queue<string>? _groupedResults; // Results from executing grouped sequence

    // Prefix mappings for expansion
    private readonly PrefixMapping[]? _prefixes;
    // Expanded IRIs stored as strings to ensure span lifetime safety
    // Each position needs its own storage since Initialize() resolves all three
    // before calling ExecuteTemporalQuery
    private string? _expandedSubject;
    private string? _expandedPredicate;
    private string? _expandedObject;

    public TriplePatternScan(QuadStore store, ReadOnlySpan<char> source,
        TriplePattern pattern, BindingTable initialBindings, ReadOnlySpan<char> graph = default)
        : this(store, source, pattern, initialBindings, graph,
               TemporalQueryMode.Current, default, default, default, null)
    {
    }

    public TriplePatternScan(QuadStore store, ReadOnlySpan<char> source,
        TriplePattern pattern, BindingTable initialBindings, ReadOnlySpan<char> graph,
        TemporalQueryMode temporalMode, DateTimeOffset asOfTime,
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd,
        PrefixMapping[]? prefixes = null)
    {
        _store = store;
        _source = source;
        _pattern = pattern;
        _initialBindings = initialBindings;
        _initialBindingsCount = initialBindings.Count;
        _graph = graph;
        _initialized = false;
        _enumerator = default;

        // Temporal parameters
        _temporalMode = temporalMode;
        _asOfTime = asOfTime;
        _rangeStart = rangeStart;
        _rangeEnd = rangeEnd;

        // Check property path type
        _isInverse = pattern.Path.Type == PathType.Inverse;
        _isZeroOrMore = pattern.Path.Type == PathType.ZeroOrMore;
        _isOneOrMore = pattern.Path.Type == PathType.OneOrMore;
        _isZeroOrOne = pattern.Path.Type == PathType.ZeroOrOne;
        _isAlternative = pattern.Path.Type == PathType.Alternative;
        _isNegatedSet = pattern.Path.Type == PathType.NegatedSet;
        _isGroupedZeroOrMore = pattern.Path.Type == PathType.GroupedZeroOrMore;
        _isGroupedOneOrMore = pattern.Path.Type == PathType.GroupedOneOrMore;
        _isGroupedZeroOrOne = pattern.Path.Type == PathType.GroupedZeroOrOne;
        _isInverseGroup = pattern.Path.Type == PathType.InverseGroup;

        _visited = null;
        _frontier = null;
        _currentNode = null;
        _startNode = null;
        _emittedReflexive = false;
        _startingNodes = null;
        _allNodes = null;
        _reflexiveIndex = 0;
        _inReflexivePhase = false;
        _reflexiveList = null;
        _alternativePhase = 0;
        _alternativeRemainingStart = 0;
        _alternativeRemainingLength = 0;
        _inSequencePhase = false;
        _sequenceFirstStepIsInverse = false;
        _sequenceIntermediates = null;
        _negatedSetPhase = 0;
        _negatedSetHasInverse = false;
        _negatedSetHasDirect = false;
        _groupedResults = null;

        _prefixes = prefixes;
        _expandedSubject = null;
        _expandedPredicate = null;
        _expandedObject = null;
    }

    public bool MoveNext(ref BindingTable bindings)
    {
        if (!_initialized)
        {
            Initialize();
            _initialized = true;
        }

        // Handle transitive paths with BFS
        if (_isZeroOrMore || _isOneOrMore || _isGroupedZeroOrMore || _isGroupedOneOrMore)
        {
            return MoveNextTransitive(ref bindings);
        }

        // Handle inverse grouped path ^(p1/p2) - iterate over pre-computed results
        if (_isInverseGroup && _groupedResults != null)
        {
            while (_groupedResults.Count > 0)
            {
                var result = _groupedResults.Dequeue();
                bindings.TruncateTo(_initialBindingsCount);

                // For ^(path), the subject in the pattern is where we started,
                // and the result is what we found - bind appropriately
                var subject = ResolveTermForQuery(_pattern.Subject);
                if (!subject.IsEmpty)
                {
                    // Subject was bound (e.g., in:c ^(p1/p2) ?x) - bind result to object
                    if (TryBindVariable(_pattern.Object, result.AsSpan(), ref bindings))
                        return true;
                }
                else
                {
                    // Object was bound (e.g., ?x ^(p1/p2) in:c) - bind result to subject
                    if (TryBindVariable(_pattern.Subject, result.AsSpan(), ref bindings))
                        return true;
                }
            }
            return false;
        }

        // Handle grouped zero-or-one path (p1/p2)? - iterate over sequence results, then emit reflexive
        if (_isGroupedZeroOrOne)
        {
            // First, iterate over the sequence results (one occurrence of the path)
            while (_groupedResults != null && _groupedResults.Count > 0)
            {
                var result = _groupedResults.Dequeue();
                bindings.TruncateTo(_initialBindingsCount);

                // Bind the result to the object variable
                if (TryBindVariable(_pattern.Subject, _startNode!.AsSpan(), ref bindings) &&
                    TryBindVariable(_pattern.Object, result.AsSpan(), ref bindings))
                {
                    return true;
                }
            }

            // Then emit the reflexive case (zero occurrences: subject = object)
            if (!_emittedReflexive && !string.IsNullOrEmpty(_startNode))
            {
                _emittedReflexive = true;
                bindings.TruncateTo(_initialBindingsCount);

                if (TryBindVariable(_pattern.Subject, _startNode.AsSpan(), ref bindings) &&
                    TryBindVariable(_pattern.Object, _startNode.AsSpan(), ref bindings))
                {
                    return true;
                }
            }

            return false;
        }

        while (true)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            while (_enumerator.MoveNext())
            {
                var triple = _enumerator.Current;

                // Preserve initial bindings (from subquery join), only clear added bindings
                bindings.TruncateTo(_initialBindingsCount);

                if (_isInverse)
                {
                    // For inverse, swap subject and object bindings
                    if (!TryBindVariable(_pattern.Subject, triple.Object, ref bindings))
                        continue;
                    if (!TryBindVariable(_pattern.Object, triple.Subject, ref bindings))
                        continue;
                }
                else if (_isNegatedSet)
                {
                    // Negated property sets support both direct and inverse predicates.
                    // Direct predicates (e.g., !ex:p) filter ?s ?p ?o with normal binding
                    // Inverse predicates (e.g., !^ex:p) filter ?s ?p ?o with inverted binding (?s←o, ?o←s)
                    if (_negatedSetPhase == 0)
                    {
                        // Direct phase: exclude triples where predicate matches direct negated set
                        if (_negatedSetHasDirect && IsPredicateInDirectNegatedSet(triple.Predicate))
                            continue;

                        // Skip direct phase entirely if set has ONLY inverse predicates
                        if (!_negatedSetHasDirect)
                            continue;

                        // Bind normally: subject → subject, object → object
                        if (!TryBindVariable(_pattern.Subject, triple.Subject, ref bindings))
                            continue;
                        if (!TryBindVariable(_pattern.Object, triple.Object, ref bindings))
                            continue;
                    }
                    else
                    {
                        // Inverse phase: exclude triples where predicate matches inverse negated set
                        if (IsPredicateInInverseNegatedSet(triple.Predicate))
                            continue;

                        // Bind inverted: subject → object, object → subject
                        if (!TryBindVariable(_pattern.Subject, triple.Object, ref bindings))
                            continue;
                        if (!TryBindVariable(_pattern.Object, triple.Subject, ref bindings))
                            continue;
                    }
                }
                else if (_isAlternative)
                {
                    // For alternative path, handling depends on whether we're in a sequence phase
                    if (_inSequencePhase && _sequenceIntermediates != null)
                    {
                        // First step of sequence - collect intermediate, don't return yet
                        // For inverse first step (^pred), we found ?x pred :start, so collect Subject
                        // For direct first step, we found :start pred ?x, so collect Object
                        var intermediate = _sequenceFirstStepIsInverse ? triple.Subject : triple.Object;
                        _sequenceIntermediates.Enqueue(intermediate.ToString());
                        continue;
                    }

                    // For second step of sequence, only bind object (subject is intermediate, not pattern subject)
                    if (!_inSequenceSecondStep)
                    {
                        if (!TryBindVariable(_pattern.Subject, triple.Subject, ref bindings))
                        {
                            continue;
                        }
                    }
                    if (!TryBindVariable(_pattern.Object, triple.Object, ref bindings))
                    {
                        continue;
                    }
                }
                else
                {
                    // Normal binding
                    if (!TryBindVariable(_pattern.Subject, triple.Subject, ref bindings))
                        continue;
                    if (!TryBindVariable(_pattern.Predicate, triple.Predicate, ref bindings))
                        continue;
                    if (!TryBindVariable(_pattern.Object, triple.Object, ref bindings))
                        continue;
                }

                return true;
            }

            // Current predicate/phase exhausted - check for alternative path transitions
            if (_isAlternative)
            {
                // If we were collecting sequence intermediates, now query the second step
                if (_inSequencePhase && _sequenceIntermediates != null && _sequenceIntermediates.Count > 0)
                {
                    _enumerator.Dispose();
                    _inSequencePhase = false;
                    _inSequenceSecondStep = true;  // Mark that we're in second step (skip subject binding)

                    // Get the second predicate of the sequence (stored in _expandedPredicate)
                    var secondPred = _expandedPredicate != null ? _expandedPredicate.AsSpan() : ReadOnlySpan<char>.Empty;
                    secondPred = ExpandPathPredicateSpan(secondPred);
                    var obj = ResolveTermForQuery(_pattern.Object);

                    // Query from each intermediate to the final object
                    // We'll process intermediates one at a time
                    var intermediate = _sequenceIntermediates.Dequeue();
                    _enumerator = ExecuteTemporalQuery(intermediate.AsSpan(), secondPred, obj);
                    continue;
                }

                // If we have more intermediates to process (from sequence), do them
                if (!_inSequencePhase && _sequenceIntermediates != null && _sequenceIntermediates.Count > 0)
                {
                    _enumerator.Dispose();
                    var secondPred = _expandedPredicate != null ? _expandedPredicate.AsSpan() : ReadOnlySpan<char>.Empty;
                    secondPred = ExpandPathPredicateSpan(secondPred);
                    var obj = ResolveTermForQuery(_pattern.Object);
                    var intermediate = _sequenceIntermediates.Dequeue();
                    _enumerator = ExecuteTemporalQuery(intermediate.AsSpan(), secondPred, obj);
                    continue;
                }

                // Current segment exhausted - move to next segment in remaining span
                if (_alternativeRemainingLength > 0)
                {
                    _alternativePhase++;
                    _enumerator.Dispose();

                    // Find next segment (up to next | or end)
                    var remainingSpan = _source.Slice(_alternativeRemainingStart, _alternativeRemainingLength);
                    var pipeIdx = FindTopLevelOperator(remainingSpan, '|');

                    ReadOnlySpan<char> currentSegment;
                    if (pipeIdx >= 0)
                    {
                        currentSegment = remainingSpan.Slice(0, pipeIdx);
                        _alternativeRemainingStart += pipeIdx + 1;
                        _alternativeRemainingLength -= pipeIdx + 1;
                    }
                    else
                    {
                        currentSegment = remainingSpan;
                        _alternativeRemainingLength = 0; // No more segments
                    }

                    // Check if this segment is a sequence
                    var slashIdx = FindTopLevelOperator(currentSegment, '/');
                    var subject = ResolveTermForQuery(_pattern.Subject);
                    var obj = ResolveTermForQuery(_pattern.Object);

                    if (slashIdx >= 0)
                    {
                        // Sequence segment - execute first step, collect intermediates
                        var firstPred = currentSegment.Slice(0, slashIdx);
                        var secondPred = currentSegment.Slice(slashIdx + 1);

                        // Check if first predicate is inverse (^pred)
                        bool firstIsInverse = firstPred.Length > 0 && firstPred[0] == '^';
                        if (firstIsInverse)
                            firstPred = firstPred.Slice(1); // Strip ^

                        // IMPORTANT: Expand second predicate FIRST and save as string,
                        // because ExpandPathPredicateSpan overwrites _expandedPredicate
                        var expandedSecondPred = ExpandPathPredicateSpan(secondPred).ToString();

                        // Now expand first predicate (this will overwrite _expandedPredicate internally)
                        var predicate = ExpandPathPredicateSpan(firstPred);

                        // NOW store the second predicate (after first pred expansion is done)
                        _expandedPredicate = expandedSecondPred;

                        _inSequencePhase = true;
                        _inSequenceSecondStep = false;  // Reset - starting new sequence first step
                        _sequenceFirstStepIsInverse = firstIsInverse;
                        _sequenceIntermediates = new Queue<string>();

                        // For inverse first step ^pred from :a, query (*, pred, :a) to find triples where object=:a
                        // For direct first step from :a, query (:a, pred, *) to find triples where subject=:a
                        if (firstIsInverse)
                            _enumerator = ExecuteTemporalQuery(ReadOnlySpan<char>.Empty, predicate, subject);
                        else
                            _enumerator = ExecuteTemporalQuery(subject, predicate, ReadOnlySpan<char>.Empty);
                    }
                    else
                    {
                        // Simple predicate - reset sequence state
                        _inSequenceSecondStep = false;

                        // Check if predicate is inverse (^pred)
                        bool isInverse = currentSegment.Length > 0 && currentSegment[0] == '^';
                        var predSpan = isInverse ? currentSegment.Slice(1) : currentSegment;
                        var predicate = ExpandPathPredicateSpan(predSpan);

                        // For inverse, swap subject and object in query
                        if (isInverse)
                            _enumerator = ExecuteTemporalQuery(obj, predicate, subject);
                        else
                            _enumerator = ExecuteTemporalQuery(subject, predicate, obj);
                    }
                    continue;
                }
            }

            // Current scan exhausted - check for negated set inverse phase
            if (_isNegatedSet && _negatedSetPhase == 0 && _negatedSetHasInverse)
            {
                // Switch to inverse phase
                _negatedSetPhase = 1;
                _enumerator.Dispose();

                // Re-initialize with same query (scan all triples again for inverse bindings)
                var subject = ResolveTermForQuery(_pattern.Subject);
                var obj = ResolveTermForQuery(_pattern.Object);

                _enumerator = ExecuteTemporalQuery(subject, ReadOnlySpan<char>.Empty, obj);
                continue; // Try again with inverse bindings
            }

            break; // No more alternatives or phases
        }

        // For zero-or-one, also emit reflexive case if subject == object and not yet emitted
        if (_isZeroOrOne && !_emittedReflexive)
        {
            _emittedReflexive = true;
            bindings.TruncateTo(_initialBindingsCount);

            // Get subject and object values
            var subjectSpan = ResolveTermForQuery(_pattern.Subject);
            var objectSpan = ResolveTermForQuery(_pattern.Object);

            // For reflexive: if subject is bound, use it; if subject is unbound but object is bound, use object
            // This handles queries like "?s :p? :o" where the reflexive case should bind ?s = :o
            ReadOnlySpan<char> reflexiveValue;
            if (!subjectSpan.IsEmpty)
            {
                reflexiveValue = subjectSpan;
            }
            else if (!objectSpan.IsEmpty)
            {
                reflexiveValue = objectSpan;
            }
            else
            {
                reflexiveValue = ReadOnlySpan<char>.Empty;
            }

            if (!reflexiveValue.IsEmpty)
            {
                // Bind both subject and object to the same value (reflexive)
                if (TryBindVariable(_pattern.Subject, reflexiveValue, ref bindings) &&
                    TryBindVariable(_pattern.Object, reflexiveValue, ref bindings))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool MoveNextTransitive(ref BindingTable bindings)
    {
        var isGrouped = _isGroupedZeroOrMore || _isGroupedOneOrMore;

        // For multi-start mode (both subject and object unbound), emit all reflexive bindings first
        if (_inReflexivePhase && _reflexiveList != null)
        {
            while (_reflexiveIndex < _reflexiveList.Count)
            {
                var node = _reflexiveList[_reflexiveIndex++];
                bindings.TruncateTo(_initialBindingsCount);

                // Bind subject and object to same node (reflexive)
                if (TryBindVariable(_pattern.Subject, node.AsSpan(), ref bindings) &&
                    TryBindVariable(_pattern.Object, node.AsSpan(), ref bindings))
                {
                    return true;
                }
            }
            _inReflexivePhase = false;
        }

        // For single-start zero-or-more, emit reflexive for just the start node
        if ((_isZeroOrMore || _isGroupedZeroOrMore) && !_emittedReflexive && _reflexiveList == null)
        {
            _emittedReflexive = true;
            bindings.TruncateTo(_initialBindingsCount);

            // Bind subject to start node, object to start node (reflexive)
            if (!string.IsNullOrEmpty(_startNode) &&
                TryBindVariable(_pattern.Subject, _startNode.AsSpan(), ref bindings) &&
                TryBindVariable(_pattern.Object, _startNode.AsSpan(), ref bindings))
            {
                return true;
            }
        }

        // BFS for transitive closure
        while (true)
        {
            QueryCancellation.ThrowIfCancellationRequested();

            if (isGrouped)
            {
                // For grouped paths, iterate over pre-computed sequence results
                while (_groupedResults != null && _groupedResults.Count > 0)
                {
                    var targetNode = _groupedResults.Dequeue();

                    if (!_visited!.Contains(targetNode))
                    {
                        _visited.Add(targetNode);
                        _frontier!.Enqueue(targetNode);

                        bindings.TruncateTo(_initialBindingsCount);
                        // Bind subject to original start node, object to the discovered target
                        if (TryBindVariable(_pattern.Subject, _startNode.AsSpan(), ref bindings) &&
                            TryBindVariable(_pattern.Object, targetNode.AsSpan(), ref bindings))
                        {
                            return true;
                        }
                    }
                }
            }
            else
            {
                // For simple paths, use the enumerator
                while (_enumerator.MoveNext())
                {
                    var triple = _enumerator.Current;
                    var targetNode = triple.Object.ToString();

                    if (!_visited!.Contains(targetNode))
                    {
                        _visited.Add(targetNode);
                        _frontier!.Enqueue(targetNode);

                        bindings.TruncateTo(_initialBindingsCount);
                        // Bind subject to original start node, object to the discovered target
                        if (TryBindVariable(_pattern.Subject, _startNode.AsSpan(), ref bindings) &&
                            TryBindVariable(_pattern.Object, triple.Object, ref bindings))
                        {
                            return true;
                        }
                    }
                }
                _enumerator.Dispose();
            }

            // Move to next frontier node
            if (_frontier!.Count > 0)
            {
                _currentNode = _frontier.Dequeue();

                if (isGrouped)
                {
                    _groupedResults = new Queue<string>(ExecuteGroupedSequence(_currentNode));
                }
                else
                {
                    var predicate = _pattern.HasPropertyPath
                        ? ResolveTermForQuery(_pattern.Path.Iri)
                        : ResolveTermForQuery(_pattern.Predicate);

                    _enumerator = ExecuteTemporalQuery(
                        _currentNode.AsSpan(),
                        predicate,
                        ReadOnlySpan<char>.Empty);
                }
                continue;
            }

            // Frontier exhausted - check for more starting nodes (multi-start mode)
            if (_startingNodes != null && _startingNodes.Count > 0)
            {
                _startNode = _startingNodes.Dequeue();
                _visited!.Clear();
                _visited.Add(_startNode);
                _frontier!.Clear();
                _currentNode = _startNode;

                if (isGrouped)
                {
                    _groupedResults = new Queue<string>(ExecuteGroupedSequence(_startNode));
                }
                else
                {
                    var predicate = _pattern.HasPropertyPath
                        ? ResolveTermForQuery(_pattern.Path.Iri)
                        : ResolveTermForQuery(_pattern.Predicate);

                    _enumerator = ExecuteTemporalQuery(
                        _startNode.AsSpan(),
                        predicate,
                        ReadOnlySpan<char>.Empty);
                }
                continue;
            }

            return false;
        }
    }

    private void Initialize()
    {
        if (_isZeroOrMore || _isOneOrMore || _isGroupedZeroOrMore || _isGroupedOneOrMore)
        {
            InitializeTransitive();
            return;
        }

        // Resolve terms to spans for querying
        // ResolveTermWithStorage stores expanded IRIs as strings, returning spans over them
        // This ensures spans remain valid until ExecuteTemporalQuery completes
        var subject = ResolveTermWithStorage(_pattern.Subject, TermPosition.Subject);
        var obj = ResolveTermWithStorage(_pattern.Object, TermPosition.Object);

        ReadOnlySpan<char> predicate;
        if (_isInverse)
        {
            // For inverse path, query with swapped subject/object
            predicate = ResolveTermWithStorage(_pattern.Path.Iri, TermPosition.Predicate);
            _enumerator = ExecuteTemporalQuery(obj, predicate, subject);
        }
        else if (_isAlternative)
        {
            // For alternative path (p1|p2|...), start with first segment from Left offsets
            // Store remaining right span for subsequent phases (supports n-ary alternatives)
            _alternativeRemainingStart = _pattern.Path.RightStart;
            _alternativeRemainingLength = _pattern.Path.RightLength;

            // Check if the left segment is a sequence (contains / at top level)
            var leftSpan = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);
            var slashIdx = FindTopLevelOperator(leftSpan, '/');

            if (slashIdx >= 0)
            {
                // Left segment is a sequence - execute first step, collect intermediates
                var firstPred = leftSpan.Slice(0, slashIdx);
                var secondPred = leftSpan.Slice(slashIdx + 1);

                // Store second predicate for later use in MoveNext
                _expandedPredicate = secondPred.ToString();
                _sequenceIntermediates = new Queue<string>();

                // Check if first step is a grouped alternative like (:p0|^:p1)
                if (firstPred.Length > 2 && firstPred[0] == '(' && firstPred[^1] == ')')
                {
                    // Grouped alternative - execute each branch and collect all intermediates
                    var groupInner = firstPred.Slice(1, firstPred.Length - 2);
                    ExecuteGroupedAlternativeFirstStep(groupInner, subject);
                    // We've collected all intermediates, skip straight to second step
                    _inSequencePhase = false;
                    _inSequenceSecondStep = true;
                    // Start processing intermediates
                    if (_sequenceIntermediates.Count > 0)
                    {
                        var expandedSecondPred = ExpandPathPredicateSpan(secondPred);
                        var intermediate = _sequenceIntermediates.Dequeue();
                        _enumerator = ExecuteTemporalQuery(intermediate.AsSpan(), expandedSecondPred, obj);
                    }
                    else
                    {
                        // No intermediates - set up empty enumerator
                        _enumerator = ExecuteTemporalQuery("__no_match__".AsSpan(), "__no_match__".AsSpan(), "__no_match__".AsSpan());
                    }
                }
                else
                {
                    // Simple predicate - check if first predicate is inverse (^pred)
                    bool firstIsInverse = firstPred.Length > 0 && firstPred[0] == '^';
                    if (firstIsInverse)
                        firstPred = firstPred.Slice(1); // Strip ^

                    predicate = ExpandPathPredicateSpan(firstPred);
                    _inSequencePhase = true;
                    _sequenceFirstStepIsInverse = firstIsInverse;

                    // Query first step - results will be collected as intermediates
                    // For inverse first step ^pred from :a, query (*, pred, :a) to find triples where object=:a
                    // For direct first step from :a, query (:a, pred, *) to find triples where subject=:a
                    if (firstIsInverse)
                        _enumerator = ExecuteTemporalQuery(ReadOnlySpan<char>.Empty, predicate, subject);
                    else
                        _enumerator = ExecuteTemporalQuery(subject, predicate, ReadOnlySpan<char>.Empty);
                }
            }
            else
            {
                // Simple predicate - check for inverse
                bool isInverse = leftSpan.Length > 0 && leftSpan[0] == '^';
                var predSpan = isInverse ? leftSpan.Slice(1) : leftSpan;
                predicate = ExpandPathPredicateSpan(predSpan);

                // For inverse, swap subject and object in query
                if (isInverse)
                    _enumerator = ExecuteTemporalQuery(obj, predicate, subject);
                else
                    _enumerator = ExecuteTemporalQuery(subject, predicate, obj);
            }
        }
        else if (_isNegatedSet)
        {
            // For negated property set !(p1|^p2|...), query all predicates (wildcard)
            // and filter in MoveNext. Analyze set for direct vs inverse predicates.
            _negatedSetHasDirect = NegatedSetHasDirectPredicates();
            _negatedSetHasInverse = NegatedSetHasInversePredicates();
            _negatedSetPhase = 0;  // Start with direct scan phase
            _enumerator = ExecuteTemporalQuery(subject, ReadOnlySpan<char>.Empty, obj);
        }
        else if (_isGroupedZeroOrOne)
        {
            // For grouped zero-or-one path (p1/p2)?, execute the sequence once
            // and store results in _groupedResults. Also need to emit reflexive case (subject=object).
            // Property paths use set semantics, so deduplicate the results.
            var startNodeSpan = subject.IsEmpty ? obj : subject;
            var expandedStart = ExpandPrefixedName(startNodeSpan);
            _startNode = expandedStart.ToString();
            var sequenceResults = ExecuteGroupedSequence(_startNode);
            // Deduplicate and exclude reflexive (we'll emit it separately)
            // Note: Copy _startNode to local to avoid capturing 'this' in lambda
            var startNode = _startNode;
            _groupedResults = new Queue<string>(sequenceResults.Where(r => r != startNode).Distinct());
        }
        else if (_isInverseGroup)
        {
            // For inverse grouped path ^(p1/p2), execute the sequence in reverse
            // The subject is the start node, results go to _groupedResults for iteration in MoveNext
            // Expand prefixed names to full IRIs for store queries
            var startNodeSpan = subject.IsEmpty ? obj : subject;
            var expandedStart = ExpandPrefixedName(startNodeSpan);
            var startNode = expandedStart.ToString();
            _groupedResults = new Queue<string>(ExecuteInverseGroupedSequence(startNode));
        }
        else
        {
            predicate = ResolveTermWithStorage(_pattern.Predicate, TermPosition.Predicate);
            _enumerator = ExecuteTemporalQuery(subject, predicate, obj);
        }
    }

    private enum TermPosition { Subject, Predicate, Object }

    private TemporalResultEnumerator ExecuteTemporalQuery(
        ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        return _temporalMode switch
        {
            TemporalQueryMode.AsOf =>
                _store.QueryAsOf(subject, predicate, obj, _asOfTime, _graph),
            TemporalQueryMode.During =>
                _store.QueryChanges(_rangeStart, _rangeEnd, subject, predicate, obj, _graph),
            TemporalQueryMode.AllVersions =>
                _store.QueryEvolution(subject, predicate, obj, _graph),
            _ => _store.QueryCurrent(subject, predicate, obj, _graph)
        };
    }

    /// <summary>
    /// Executes a grouped sequence path from a given start node.
    /// For example, for (p1/p2/p3)*, this executes the full sequence p1→p2→p3
    /// and returns all nodes reachable at the end of the sequence.
    /// </summary>
    private List<string> ExecuteGroupedSequence(string startNode)
    {
        var results = new List<string>();

        // Get the inner content of the grouped path
        var innerContent = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);

        // Parse the sequence into individual predicates
        var predicates = new List<ReadOnlyMemory<char>>();
        var contentStr = innerContent.ToString();
        var start = 0;
        var depth = 0;

        for (int i = 0; i < contentStr.Length; i++)
        {
            var ch = contentStr[i];
            if (ch == '<') depth++;
            else if (ch == '>') depth--;
            else if (ch == '/' && depth == 0)
            {
                if (i > start)
                {
                    predicates.Add(contentStr.AsMemory(start, i - start));
                }
                start = i + 1;
            }
        }
        // Add the last predicate
        if (start < contentStr.Length)
        {
            predicates.Add(contentStr.AsMemory(start, contentStr.Length - start));
        }

        if (predicates.Count == 0)
            return results;

        // Execute the sequence: start with the start node, execute each predicate in turn
        var currentNodes = new List<string> { startNode };

        foreach (var predicateMem in predicates)
        {
            var nextNodes = new List<string>();
            var predicate = ExpandPrefixedName(predicateMem.Span);

            foreach (var node in currentNodes)
            {
                var enumerator = ExecuteTemporalQuery(
                    node.AsSpan(),
                    predicate,
                    ReadOnlySpan<char>.Empty);

                while (enumerator.MoveNext())
                {
                    var triple = enumerator.Current;
                    nextNodes.Add(triple.Object.ToString());
                }
                enumerator.Dispose();
            }

            currentNodes = nextNodes;
            if (currentNodes.Count == 0)
                break;
        }

        results.AddRange(currentNodes);
        return results;
    }

    /// <summary>
    /// Executes an inverse grouped sequence path from a given start node.
    /// For example, for ^(p1/p2), this executes the sequence in reverse with inverse predicates: ^p2→^p1.
    /// This finds nodes that lead TO the start node via the original sequence.
    /// </summary>
    private List<string> ExecuteInverseGroupedSequence(string startNode)
    {
        var results = new List<string>();

        // Get the inner content of the grouped path
        var innerContent = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);

        // Parse the sequence into individual predicates
        var predicates = new List<ReadOnlyMemory<char>>();
        var contentStr = innerContent.ToString();
        var start = 0;
        var depth = 0;

        for (int i = 0; i < contentStr.Length; i++)
        {
            var ch = contentStr[i];
            if (ch == '<') depth++;
            else if (ch == '>') depth--;
            else if (ch == '/' && depth == 0)
            {
                if (i > start)
                {
                    predicates.Add(contentStr.AsMemory(start, i - start));
                }
                start = i + 1;
            }
        }
        // Add the last predicate
        if (start < contentStr.Length)
        {
            predicates.Add(contentStr.AsMemory(start, contentStr.Length - start));
        }

        if (predicates.Count == 0)
            return results;

        // REVERSE the predicate order for inverse execution
        predicates.Reverse();

        // Execute the sequence: start with the start node, execute each predicate in INVERSE direction
        var currentNodes = new List<string> { startNode };

        foreach (var predicateMem in predicates)
        {
            var nextNodes = new List<string>();
            var predicate = ExpandPrefixedName(predicateMem.Span);

            foreach (var node in currentNodes)
            {
                // INVERSE: query with node as OBJECT to find subjects
                var enumerator = ExecuteTemporalQuery(
                    ReadOnlySpan<char>.Empty,
                    predicate,
                    node.AsSpan());

                while (enumerator.MoveNext())
                {
                    var triple = enumerator.Current;
                    nextNodes.Add(triple.Subject.ToString());
                }
                enumerator.Dispose();
            }

            currentNodes = nextNodes;
            if (currentNodes.Count == 0)
                break;
        }

        results.AddRange(currentNodes);
        return results;
    }

    /// <summary>
    /// Discovers all starting nodes that have the first predicate of a grouped sequence.
    /// Returns (subjects, allNodes) where subjects can start the sequence and allNodes
    /// includes all nodes seen (for reflexive bindings in zero-or-more).
    /// </summary>
    private (HashSet<string> subjects, HashSet<string> allNodes) DiscoverGroupedSequenceStartNodes()
    {
        var subjects = new HashSet<string>();
        var allNodes = new HashSet<string>();

        // Get the inner content and find the first predicate
        var innerContent = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);
        var contentStr = innerContent.ToString();

        // Find the first predicate (up to the first / at depth 0)
        var depth = 0;
        var firstPredicateEnd = contentStr.Length;
        for (int i = 0; i < contentStr.Length; i++)
        {
            var ch = contentStr[i];
            if (ch == '<') depth++;
            else if (ch == '>') depth--;
            else if (ch == '/' && depth == 0)
            {
                firstPredicateEnd = i;
                break;
            }
        }

        var firstPredicate = ExpandPrefixedName(contentStr.AsSpan(0, firstPredicateEnd));

        // Query all triples with the first predicate
        var enumerator = ExecuteTemporalQuery(
            ReadOnlySpan<char>.Empty,
            firstPredicate,
            ReadOnlySpan<char>.Empty);

        while (enumerator.MoveNext())
        {
            var triple = enumerator.Current;
            var subjectStr = triple.Subject.ToString();
            var objectStr = triple.Object.ToString();

            allNodes.Add(subjectStr);
            allNodes.Add(objectStr);
            subjects.Add(subjectStr);
        }
        enumerator.Dispose();

        return (subjects, allNodes);
    }

    private void InitializeTransitive()
    {
        _visited = new HashSet<string>();
        _frontier = new Queue<string>();

        var subject = ResolveTermForQuery(_pattern.Subject);
        var obj = ResolveTermForQuery(_pattern.Object);

        // For grouped paths, we use ExecuteGroupedSequence instead of a single predicate query
        var isGrouped = _isGroupedZeroOrMore || _isGroupedOneOrMore;

        var predicate = ReadOnlySpan<char>.Empty;
        if (!isGrouped)
        {
            predicate = _pattern.HasPropertyPath
                ? ResolveTermForQuery(_pattern.Path.Iri)
                : ResolveTermForQuery(_pattern.Predicate);
        }

        // Case 1: Both subject and object are unbound - need to discover all starting nodes
        if (subject.IsEmpty && obj.IsEmpty)
        {
            _startingNodes = new Queue<string>();
            _allNodes = new HashSet<string>();
            HashSet<string> subjects;

            if (isGrouped)
            {
                // For grouped paths, use the helper to discover starting nodes
                (subjects, _allNodes) = DiscoverGroupedSequenceStartNodes();
            }
            else
            {
                subjects = new HashSet<string>();
                // Query all triples with the predicate to discover all subjects and objects
                var discoveryEnumerator = ExecuteTemporalQuery(
                    ReadOnlySpan<char>.Empty,
                    predicate,
                    ReadOnlySpan<char>.Empty);

                while (discoveryEnumerator.MoveNext())
                {
                    var triple = discoveryEnumerator.Current;
                    var subjectStr = triple.Subject.ToString();
                    var objectStr = triple.Object.ToString();

                    // Track all nodes (for reflexive)
                    _allNodes.Add(subjectStr);
                    _allNodes.Add(objectStr);

                    // Track subjects separately for starting nodes
                    subjects.Add(subjectStr);
                }
                discoveryEnumerator.Dispose();

                // For zero-or-more (*) paths, SPARQL spec requires reflexive pairs for ALL nodes
                // in the graph, not just those connected by the predicate. Query all triples
                // to discover all nodes in the graph.
                if (_isZeroOrMore)
                {
                    var allTriplesEnumerator = ExecuteTemporalQuery(
                        ReadOnlySpan<char>.Empty,
                        ReadOnlySpan<char>.Empty,
                        ReadOnlySpan<char>.Empty);

                    while (allTriplesEnumerator.MoveNext())
                    {
                        var triple = allTriplesEnumerator.Current;
                        _allNodes.Add(triple.Subject.ToString());
                        _allNodes.Add(triple.Object.ToString());
                    }
                    allTriplesEnumerator.Dispose();
                }
            }

            // Add all subjects as starting nodes
            foreach (var s in subjects)
            {
                _startingNodes.Enqueue(s);
            }

            // For zero-or-more, we'll emit reflexive for ALL nodes first
            _emittedReflexive = false;
            _inReflexivePhase = _isZeroOrMore || _isGroupedZeroOrMore;
            if (_isZeroOrMore || _isGroupedZeroOrMore)
            {
                _reflexiveList = new List<string>(_allNodes);
                _reflexiveIndex = 0;
            }

            // Start processing first starting node (if any)
            if (_startingNodes.Count > 0)
            {
                _startNode = _startingNodes.Dequeue();
                _visited.Clear();
                _visited.Add(_startNode);
                _currentNode = _startNode;

                if (isGrouped)
                {
                    // For grouped paths, execute the sequence and queue results
                    _groupedResults = new Queue<string>(ExecuteGroupedSequence(_startNode));
                }
                else
                {
                    _enumerator = ExecuteTemporalQuery(
                        _startNode.AsSpan(),
                        predicate,
                        ReadOnlySpan<char>.Empty);
                }
            }
            else
            {
                // No triples found - just emit reflexive if zero-or-more
                _startNode = "";
                _enumerator = default;
                _groupedResults = new Queue<string>();
            }
        }
        // Case 2: Subject is unbound but object is bound - use object as start
        else if (subject.IsEmpty && !obj.IsEmpty)
        {
            _startNode = obj.ToString();
            _emittedReflexive = false;

            _visited.Add(_startNode);
            _currentNode = _startNode;

            if (isGrouped)
            {
                _groupedResults = new Queue<string>(ExecuteGroupedSequence(_startNode));
            }
            else
            {
                _enumerator = ExecuteTemporalQuery(
                    _startNode.AsSpan(),
                    predicate,
                    ReadOnlySpan<char>.Empty);
            }
        }
        // Case 3: Subject is bound (normal case)
        else
        {
            _startNode = subject.ToString();
            _emittedReflexive = false;

            _visited.Add(_startNode);
            _currentNode = _startNode;

            if (isGrouped)
            {
                _groupedResults = new Queue<string>(ExecuteGroupedSequence(_startNode));
            }
            else
            {
                _enumerator = ExecuteTemporalQuery(
                    _startNode.AsSpan(),
                    predicate,
                    ReadOnlySpan<char>.Empty);
            }
        }
    }

    /// <summary>
    /// Resolve term for query with position-based string storage.
    /// Expanded IRIs are stored as strings in position-specific fields to ensure
    /// span lifetime safety when multiple terms are resolved simultaneously.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermWithStorage(Term term, TermPosition position)
    {
        // Handle synthetic terms (negative offsets from SPARQL-star expansion)
        if (SyntheticTermHelper.IsSynthetic(term.Start))
        {
            if (term.IsVariable)
            {
                var varName = SyntheticTermHelper.GetSyntheticVarName(term.Start);
                var idx = _initialBindings.FindBinding(varName);
                if (idx >= 0)
                    return _initialBindings.GetString(idx);
                return ReadOnlySpan<char>.Empty;
            }
            return SyntheticTermHelper.GetSyntheticIri(term.Start);
        }

        if (term.IsVariable)
        {
            var varName = _source.Slice(term.Start, term.Length);
            var idx = _initialBindings.FindBinding(varName);
            if (idx >= 0)
                return _initialBindings.GetString(idx);
            return ReadOnlySpan<char>.Empty;
        }

        if (term.IsBlankNode)
        {
            // Named blank nodes (_:name) should match the literal value
            // Anonymous blank nodes ([]) act as wildcards
            var bnSpan = _source.Slice(term.Start, term.Length);
            if (bnSpan.Length >= 2 && bnSpan[0] == '_' && bnSpan[1] == ':')
                return bnSpan; // Return the actual blank node label
            return ReadOnlySpan<char>.Empty; // Anonymous blank node - wildcard
        }

        var termSpan = _source.Slice(term.Start, term.Length);

        // Handle 'a' shorthand for rdf:type (SPARQL keyword)
        if (termSpan.Length == 1 && termSpan[0] == 'a')
        {
            return SyntheticTermHelper.RdfType.AsSpan();
        }

        // Check if this is a prefixed name that needs expansion
        if (_prefixes != null && termSpan.Length > 0 && termSpan[0] != '<' && termSpan[0] != '"')
        {
            var colonIdx = termSpan.IndexOf(':');
            if (colonIdx >= 0)
            {
                var prefix = termSpan.Slice(0, colonIdx + 1);
                var localName = termSpan.Slice(colonIdx + 1);

                for (int i = 0; i < _prefixes.Length; i++)
                {
                    var mapping = _prefixes[i];
                    var mappedPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);

                    if (prefix.SequenceEqual(mappedPrefix))
                    {
                        var iriNs = _source.Slice(mapping.IriStart, mapping.IriLength);
                        var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1);

                        // Build expanded IRI and store as string
                        var expanded = string.Concat(nsWithoutClose, localName, ">");

                        // Store in position-specific field and return span over it
                        switch (position)
                        {
                            case TermPosition.Subject:
                                _expandedSubject = expanded;
                                return _expandedSubject.AsSpan();
                            case TermPosition.Predicate:
                                _expandedPredicate = expanded;
                                return _expandedPredicate.AsSpan();
                            default:
                                _expandedObject = expanded;
                                return _expandedObject.AsSpan();
                        }
                    }
                }
            }
        }

        return termSpan;
    }

    /// <summary>
    /// Simple term resolution for single-term use cases (transitive paths).
    /// Uses shared buffer since only one term is resolved at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermForQuery(Term term)
    {
        if (SyntheticTermHelper.IsSynthetic(term.Start))
        {
            if (term.IsVariable)
            {
                var varName = SyntheticTermHelper.GetSyntheticVarName(term.Start);
                var idx = _initialBindings.FindBinding(varName);
                if (idx >= 0)
                    return _initialBindings.GetString(idx);
                return ReadOnlySpan<char>.Empty;
            }
            return SyntheticTermHelper.GetSyntheticIri(term.Start);
        }

        if (term.IsVariable)
        {
            var varName = _source.Slice(term.Start, term.Length);
            var idx = _initialBindings.FindBinding(varName);
            if (idx >= 0)
                return _initialBindings.GetString(idx);
            return ReadOnlySpan<char>.Empty;
        }

        if (term.IsBlankNode)
        {
            // Named blank nodes (_:name) should match the literal value
            // Anonymous blank nodes ([]) act as wildcards
            var bnSpan = _source.Slice(term.Start, term.Length);
            if (bnSpan.Length >= 2 && bnSpan[0] == '_' && bnSpan[1] == ':')
                return bnSpan; // Return the actual blank node label
            return ReadOnlySpan<char>.Empty; // Anonymous blank node - wildcard
        }

        var termSpan = _source.Slice(term.Start, term.Length);

        // Handle 'a' shorthand for rdf:type (SPARQL keyword)
        if (termSpan.Length == 1 && termSpan[0] == 'a')
        {
            return SyntheticTermHelper.RdfType.AsSpan();
        }

        // Check for prefix expansion
        if (_prefixes != null && termSpan.Length > 0 && termSpan[0] != '<' && termSpan[0] != '"')
        {
            var colonIdx = termSpan.IndexOf(':');
            if (colonIdx >= 0)
            {
                var prefix = termSpan.Slice(0, colonIdx + 1);
                var localName = termSpan.Slice(colonIdx + 1);

                for (int i = 0; i < _prefixes.Length; i++)
                {
                    var mapping = _prefixes[i];
                    var mappedPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);

                    if (prefix.SequenceEqual(mappedPrefix))
                    {
                        var iriNs = _source.Slice(mapping.IriStart, mapping.IriLength);
                        var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1);

                        // For single-term resolution, store in subject field (reusable)
                        _expandedSubject = string.Concat(nsWithoutClose, localName, ">");
                        return _expandedSubject.AsSpan();
                    }
                }
            }
        }

        return termSpan;
    }

    /// <summary>
    /// Expand a path predicate (from raw start/length offsets) with prefix expansion.
    /// Used for Alternative path predicates which are stored as offsets rather than Term structs.
    /// </summary>
    private ReadOnlySpan<char> ExpandPathPredicate(int start, int length)
    {
        var predSpan = _source.Slice(start, length);

        // Handle 'a' shorthand for rdf:type
        if (predSpan.Length == 1 && predSpan[0] == 'a')
        {
            return SyntheticTermHelper.RdfType.AsSpan();
        }

        // Check if this is a prefixed name that needs expansion
        if (_prefixes != null && predSpan.Length > 0 && predSpan[0] != '<' && predSpan[0] != '"')
        {
            var colonIdx = predSpan.IndexOf(':');
            if (colonIdx >= 0)
            {
                var prefix = predSpan.Slice(0, colonIdx + 1);
                var localName = predSpan.Slice(colonIdx + 1);

                for (int i = 0; i < _prefixes.Length; i++)
                {
                    var mapping = _prefixes[i];
                    var mappedPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);

                    if (prefix.SequenceEqual(mappedPrefix))
                    {
                        var iriNs = _source.Slice(mapping.IriStart, mapping.IriLength);
                        var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1);

                        // Store expanded IRI in _expandedPredicate field
                        _expandedPredicate = string.Concat(nsWithoutClose, localName, ">");
                        return _expandedPredicate.AsSpan();
                    }
                }
            }
        }

        return predSpan;
    }

    /// <summary>
    /// Expand a path predicate from a span with prefix expansion.
    /// Used for path segments extracted during n-ary alternative processing.
    /// </summary>
    private ReadOnlySpan<char> ExpandPathPredicateSpan(ReadOnlySpan<char> predSpan)
    {
        // Handle 'a' shorthand for rdf:type
        if (predSpan.Length == 1 && predSpan[0] == 'a')
        {
            return SyntheticTermHelper.RdfType.AsSpan();
        }

        // Check if this is a prefixed name that needs expansion
        if (_prefixes != null && predSpan.Length > 0 && predSpan[0] != '<' && predSpan[0] != '"')
        {
            var colonIdx = predSpan.IndexOf(':');
            if (colonIdx >= 0)
            {
                var prefix = predSpan.Slice(0, colonIdx + 1);
                var localName = predSpan.Slice(colonIdx + 1);

                for (int i = 0; i < _prefixes.Length; i++)
                {
                    var mapping = _prefixes[i];
                    var mappedPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);

                    if (prefix.SequenceEqual(mappedPrefix))
                    {
                        var iriNs = _source.Slice(mapping.IriStart, mapping.IriLength);
                        var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1);

                        // Store expanded IRI in _expandedPredicate field
                        _expandedPredicate = string.Concat(nsWithoutClose, localName, ">");
                        return _expandedPredicate.AsSpan();
                    }
                }
            }
        }

        return predSpan;
    }

    /// <summary>
    /// Execute a grouped alternative as the first step of a sequence.
    /// Parses alternatives from the group (e.g., ":p0|^:p1") and executes each,
    /// collecting intermediates from all branches into _sequenceIntermediates.
    /// </summary>
    private void ExecuteGroupedAlternativeFirstStep(ReadOnlySpan<char> groupInner, ReadOnlySpan<char> subject)
    {
        // Split by | at top level
        var remaining = groupInner;
        while (!remaining.IsEmpty)
        {
            var pipeIdx = FindTopLevelOperator(remaining, '|');
            ReadOnlySpan<char> current;

            if (pipeIdx < 0)
            {
                current = remaining.Trim();
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                current = remaining[..pipeIdx].Trim();
                remaining = remaining[(pipeIdx + 1)..];
            }

            if (current.IsEmpty)
                continue;

            // Check if this branch is inverse (^pred)
            bool isInverse = current[0] == '^';
            var predSpan = isInverse ? current.Slice(1) : current;
            var predicate = ExpandPathPredicateSpan(predSpan);

            // Execute query for this branch
            TemporalResultEnumerator enumerator;
            if (isInverse)
            {
                // ^pred from :a: find triples where object=:a, collect subject as intermediate
                enumerator = ExecuteTemporalQuery(ReadOnlySpan<char>.Empty, predicate, subject);
            }
            else
            {
                // pred from :a: find triples where subject=:a, collect object as intermediate
                enumerator = ExecuteTemporalQuery(subject, predicate, ReadOnlySpan<char>.Empty);
            }

            // Collect all results as intermediates
            while (enumerator.MoveNext())
            {
                var triple = enumerator.Current;
                // For inverse, collect Subject; for direct, collect Object
                var intermediate = isInverse ? triple.Subject : triple.Object;
                _sequenceIntermediates!.Enqueue(intermediate.ToString());
            }
            enumerator.Dispose();
        }
    }

    /// <summary>
    /// Find the first top-level occurrence of an operator in a path span.
    /// Returns -1 if not found. Skips operators inside parentheses and IRIs.
    /// </summary>
    private static int FindTopLevelOperator(ReadOnlySpan<char> span, char op)
    {
        int depth = 0;
        bool inIri = false;
        for (int i = 0; i < span.Length; i++)
        {
            var ch = span[i];
            if (ch == '<' && !inIri) inIri = true;
            else if (ch == '>' && inIri) inIri = false;
            else if (!inIri)
            {
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == op && depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Check if a predicate is in the DIRECT elements of the negated property set.
    /// Direct elements are those NOT starting with '^'.
    /// </summary>
    private bool IsPredicateInDirectNegatedSet(ReadOnlySpan<char> predicate)
    {
        var content = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);

        var remaining = content;
        while (!remaining.IsEmpty)
        {
            var sepIndex = remaining.IndexOf('|');
            ReadOnlySpan<char> current;

            if (sepIndex < 0)
            {
                current = remaining.Trim();
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                current = remaining[..sepIndex].Trim();
                remaining = remaining[(sepIndex + 1)..];
            }

            // Skip inverse predicates (those starting with ^)
            if (current.Length > 0 && current[0] == '^')
                continue;

            // Expand and compare
            var expandedCurrent = ExpandPrefixedName(current);

            // Handle 'a' shorthand for rdf:type
            if (current.Length == 1 && current[0] == 'a')
                expandedCurrent = SyntheticTermHelper.RdfType.AsSpan();

            if (expandedCurrent.SequenceEqual(predicate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a predicate is in the INVERSE elements of the negated property set.
    /// Inverse elements are those starting with '^'.
    /// </summary>
    private bool IsPredicateInInverseNegatedSet(ReadOnlySpan<char> predicate)
    {
        var content = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);

        var remaining = content;
        while (!remaining.IsEmpty)
        {
            var sepIndex = remaining.IndexOf('|');
            ReadOnlySpan<char> current;

            if (sepIndex < 0)
            {
                current = remaining.Trim();
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                current = remaining[..sepIndex].Trim();
                remaining = remaining[(sepIndex + 1)..];
            }

            // Only process inverse predicates (those starting with ^)
            if (current.Length == 0 || current[0] != '^')
                continue;

            // Get the predicate after ^
            var innerPred = current.Slice(1).Trim();

            // Handle ^a shorthand for inverse rdf:type
            ReadOnlySpan<char> expandedInner;
            if (innerPred.Length == 1 && innerPred[0] == 'a')
                expandedInner = SyntheticTermHelper.RdfType.AsSpan();
            else
                expandedInner = ExpandPrefixedName(innerPred);

            if (expandedInner.SequenceEqual(predicate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if the negated set has any direct (non-inverse) predicates.
    /// </summary>
    private bool NegatedSetHasDirectPredicates()
    {
        var content = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);

        var remaining = content;
        while (!remaining.IsEmpty)
        {
            var sepIndex = remaining.IndexOf('|');
            ReadOnlySpan<char> current;

            if (sepIndex < 0)
            {
                current = remaining.Trim();
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                current = remaining[..sepIndex].Trim();
                remaining = remaining[(sepIndex + 1)..];
            }

            if (current.Length > 0 && current[0] != '^')
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if the negated set has any inverse predicates.
    /// </summary>
    private bool NegatedSetHasInversePredicates()
    {
        var content = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);

        var remaining = content;
        while (!remaining.IsEmpty)
        {
            var sepIndex = remaining.IndexOf('|');
            ReadOnlySpan<char> current;

            if (sepIndex < 0)
            {
                current = remaining.Trim();
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                current = remaining[..sepIndex].Trim();
                remaining = remaining[(sepIndex + 1)..];
            }

            if (current.Length > 0 && current[0] == '^')
                return true;
        }

        return false;
    }

    /// <summary>
    /// Expands a prefixed name (e.g., "ex:p1") to a full IRI (e.g., "&lt;http://example.org/p1&gt;").
    /// Returns the original span if not a prefixed name or no matching prefix found.
    /// </summary>
    private ReadOnlySpan<char> ExpandPrefixedName(ReadOnlySpan<char> term)
    {
        // If already a full IRI (starts with <), return as-is
        if (term.Length > 0 && term[0] == '<')
            return term;

        // If no prefixes available, return as-is
        if (_prefixes == null)
            return term;

        // Look for colon to identify prefixed name
        var colonIdx = term.IndexOf(':');
        if (colonIdx < 0)
            return term;

        var prefix = term.Slice(0, colonIdx + 1);
        var localName = term.Slice(colonIdx + 1);

        // Find matching prefix
        for (int i = 0; i < _prefixes.Length; i++)
        {
            var mapping = _prefixes[i];
            var mappedPrefix = _source.Slice(mapping.PrefixStart, mapping.PrefixLength);

            if (prefix.SequenceEqual(mappedPrefix))
            {
                var iriNs = _source.Slice(mapping.IriStart, mapping.IriLength);
                var nsWithoutClose = iriNs.Slice(0, iriNs.Length - 1);

                // Build expanded IRI and store for span lifetime
                _expandedPredicate = string.Concat(nsWithoutClose, localName, ">");
                return _expandedPredicate.AsSpan();
            }
        }

        return term;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryBindVariable(Term term, ReadOnlySpan<char> value, ref BindingTable bindings)
    {
        if (!term.IsVariable)
        {
            // Constant - verify it matches the expected value
            var expected = ResolveTermForQuery(term);
            return value.SequenceEqual(expected);
        }

        // Handle synthetic variables (from SPARQL-star expansion)
        ReadOnlySpan<char> varName;
        if (SyntheticTermHelper.IsSynthetic(term.Start))
            varName = SyntheticTermHelper.GetSyntheticVarName(term.Start);
        else
            varName = _source.Slice(term.Start, term.Length);

        // Check if already bound (from earlier pattern in join)
        var existingIndex = bindings.FindBinding(varName);
        if (existingIndex >= 0)
        {
            // Must match existing binding
            var existingValue = bindings.GetString(existingIndex);
            return value.SequenceEqual(existingValue);
        }

        // Bind new variable
        bindings.Bind(varName, value);
        return true;
    }

    public void Dispose()
    {
        _enumerator.Dispose();
    }
}
