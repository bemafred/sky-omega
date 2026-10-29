using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SkyOmega.Mercury.Sparql.Patterns;
using SkyOmega.Mercury.Storage;
using SkyOmega.Mercury.Sparql.Types;

namespace SkyOmega.Mercury.Sparql.Execution.Operators;

/// <summary>
/// Scans a single triple pattern against the QuadStore.
/// Conforms to <see cref="IScan"/> contract (duck typing).
/// </summary>
internal ref struct TriplePatternScan
{
    private readonly QuadStore _store;
    private readonly ReadOnlySpan<char> _source;
    private readonly TriplePattern _pattern;
    private MergedGraphEnumerator _enumerator;
    private bool _initialized;
    private readonly BindingTable _initialBindings;
    private readonly int _initialBindingsCount;
    private readonly ReadOnlySpan<char> _graph;
    private readonly string[]? _defaultGraphs; // a dataset's FROM graphs, whose merge is the default graph (null: no dataset)

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
    private readonly bool _isNegatedSet;
    private readonly bool _isGroupedZeroOrMore;
    private readonly bool _isGroupedOneOrMore;
    private readonly bool _isGroupedZeroOrOne;

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

    // Zero-or-one: whether each start node is paired with itself (not a bound variable's value outside the graph)
    private bool _startMatchesItself;

    // State for negated property set with inverse predicates
    // 0 = direct scan (check direct predicates), 1 = inverse scan (check inverse predicates, swap bindings)
    private int _negatedSetPhase;
    private bool _negatedSetHasInverse;  // True if set contains inverse predicates
    private bool _negatedSetHasDirect;   // True if set contains direct predicates

    // State for grouped sequence path traversal ((p1/p2/p3)*)
    private Queue<string>? _groupedResults; // Results from executing grouped sequence

    // Whether the BFS started from the bound OBJECT position (Case 2: ?x path <obj>) rather than
    // the bound SUBJECT (Case 3: <subj> path ?x). When true:
    //   * The walker's effective direction is FLIPPED relative to the path's natural direction —
    //     `?x P+ <obj>` must walk backward from <obj> to find ancestors, not forward.
    //   * MoveNextTransitive emits bindings with Subject=targetNode and Object=_startNode (the
    //     bound object value), the inverse of the Case 3 binding shape.
    // Without this flag the binding succeeds only on the reflexive case (when the walker happens
    // to return _startNode itself), and all non-reflexive ancestors are silently filtered out.
    private bool _startedFromObject;

    // Per-step deduplication set for the property-path content walker.
    // Allocated lazily on first use and cleared between calls — avoids per-match
    // string ToString() when a target node has already been emitted in the current step.
    private HashSet<string>? _walkerStepSeen;
    // Sequence frontier sets — alternate between current/next as we walk legs.
    // Allocated lazily and reused; .Clear() between uses keeps them allocation-stable.
    private HashSet<string>? _walkerCurrent;
    private HashSet<string>? _walkerNext;

    // Prefix mappings for expansion
    private readonly PrefixMapping[]? _prefixes;
    // Expanded IRIs stored as strings to ensure span lifetime safety
    // Each position needs its own storage since Initialize() resolves all three
    // before calling ExecuteTemporalQuery
    private string? _expandedSubject;
    private string? _expandedPredicate;
    private string? _expandedObject;
    private string? _literalScratch; // ADR-044: scratch owner for canonicalized literals
    private readonly HashSet<long>? _trigramObjectCandidates; // ADR-024: text:match pre-filter candidate object atom IDs

    public TriplePatternScan(QuadStore store, ReadOnlySpan<char> source,
        TriplePattern pattern, BindingTable initialBindings, ReadOnlySpan<char> graph = default)
        : this(store, source, pattern, initialBindings, graph, null,
               TemporalQueryMode.Current, default, default, default, null)
    {
    }

    /// <param name="graph">The graph the pattern reads: a GRAPH's IRI, or empty for the default graph.</param>
    /// <param name="defaultGraphs">With an empty <paramref name="graph"/>, a dataset's FROM graphs, read as their merge
    /// (empty: FROM NAMED alone, an empty default graph); null when the query has no dataset clause, and the default
    /// graph is the store's.</param>
    public TriplePatternScan(QuadStore store, ReadOnlySpan<char> source,
        TriplePattern pattern, BindingTable initialBindings, ReadOnlySpan<char> graph, string[]? defaultGraphs,
        TemporalQueryMode temporalMode, DateTimeOffset asOfTime,
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd,
        PrefixMapping[]? prefixes = null, HashSet<long>? trigramObjectCandidates = null)
    {
        _trigramObjectCandidates = trigramObjectCandidates;
        _store = store;
        _source = source;
        _pattern = pattern;
        _initialBindings = initialBindings;
        _initialBindingsCount = initialBindings.Count;
        _graph = graph;
        _defaultGraphs = defaultGraphs;
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
        _isNegatedSet = pattern.Path.Type == PathType.NegatedSet;
        _isGroupedZeroOrMore = pattern.Path.Type == PathType.GroupedZeroOrMore;
        _isGroupedOneOrMore = pattern.Path.Type == PathType.GroupedOneOrMore;
        _isGroupedZeroOrOne = pattern.Path.Type == PathType.GroupedZeroOrOne;

        // An alternative and ^( P ) are translated when the query is parsed (SparqlParser.AddPathTriple): a UNION of
        // branches, and P with its ends swapped
        if (pattern.Path.Type is PathType.Alternative or PathType.InverseGroup)
            throw new InvalidOperationException($"A {pattern.Path.Type} path is translated when the query is parsed");

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

        if (_isZeroOrOne || _isGroupedZeroOrOne)
            return MoveNextZeroOrOne(ref bindings);

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

            break; // No more phases
        }

        return false;
    }

    /// <summary>
    /// A zero-or-one path, <c>p?</c> or <c>( P )?</c>: the start node paired with itself, and with each node one step of
    /// the path reaches from it, as a set (SPARQL 1.1 §18.6). The start node is the bound end, the walk going backward
    /// when that is the object; with both ends unbound every node of the graph is one, so each is paired with itself
    /// (Path(?x, ZeroOrOnePath(P), ?y): "yn in nodes(G) and xn = yn"). A value bound to a variable is paired with itself
    /// only when it is a node of the graph; a constant always is, being a term of the query (§9.3). Through 1.8.30 both
    /// ends unbound gave no zero-length match, and a bound object end of ( P )? was walked forward.
    /// </summary>
    private void InitializeZeroOrOne(ReadOnlySpan<char> subject, ReadOnlySpan<char> obj)
    {
        _startingNodes = new Queue<string>();
        _groupedResults = new Queue<string>();
        if (subject.IsEmpty && obj.IsEmpty)
        {
            var nodes = new HashSet<string>();
            AddGraphNodes(nodes);
            foreach (var node in nodes)
                _startingNodes.Enqueue(node);
            _startMatchesItself = true; // every start is a node of the graph
            return;
        }

        _startedFromObject = subject.IsEmpty;
        var bound = _startedFromObject ? obj : subject;
        _startingNodes.Enqueue(ExpandPrefixedName(bound).ToString());
        var boundTerm = _startedFromObject ? _pattern.Object : _pattern.Subject;
        _startMatchesItself = !boundTerm.IsVariable || IsGraphNode(bound);
    }

    private bool MoveNextZeroOrOne(ref BindingTable bindings)
    {
        while (true)
        {
            while (_groupedResults!.Count > 0)
            {
                var reached = _groupedResults.Dequeue();
                bindings.TruncateTo(_initialBindingsCount);
                var subjectValue = _startedFromObject ? reached.AsSpan() : _startNode.AsSpan();
                var objectValue = _startedFromObject ? _startNode.AsSpan() : reached.AsSpan();
                if (TryBindVariable(_pattern.Subject, subjectValue, ref bindings) &&
                    TryBindVariable(_pattern.Object, objectValue, ref bindings))
                    return true;
            }
            if (_startingNodes!.Count == 0)
                return false;
            QueryCancellation.ThrowIfCancellationRequested();

            _startNode = _startingNodes.Dequeue();
            var reachedOnce = new HashSet<string>();
            if (_startMatchesItself)
            {
                reachedOnce.Add(_startNode); // zero steps
                _groupedResults.Enqueue(_startNode);
            }
            // one step: p, or the group's path; ^p? and ^( P )? are groups walked in inverse
            var step = _isZeroOrOne
                ? _source.Slice(_pattern.Path.Iri.Start, _pattern.Path.Iri.Length)
                : _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);
            bool inverse = (_isGroupedZeroOrOne && _pattern.Path.IsInverseGroup) ^ _startedFromObject;
            WalkOneBranchInto(_startNode, step, inverse, _groupedResults, reachedOnce);
        }
    }

    /// <summary>Adds every node of the graph the pattern reads, each subject and object, to <paramref name="nodes"/>.</summary>
    private void AddGraphNodes(HashSet<string> nodes)
    {
        var everyTriple = ExecuteTemporalQuery(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        try
        {
            var lookup = nodes.GetAlternateLookup<ReadOnlySpan<char>>();
            while (everyTriple.MoveNext())
            {
                QueryCancellation.ThrowIfCancellationRequested();
                var triple = everyTriple.Current;
                if (!lookup.Contains(triple.Subject)) nodes.Add(triple.Subject.ToString());
                if (!lookup.Contains(triple.Object)) nodes.Add(triple.Object.ToString());
            }
        }
        finally
        {
            everyTriple.Dispose();
        }
    }

    /// <summary>
    /// True if <paramref name="term"/> is a node of the active graph — the subject or object of at least one triple.
    /// Used to gate the zero-length-path reflexive match (SPARQL §9.3).
    /// </summary>
    private bool IsGraphNode(ReadOnlySpan<char> term)
    {
        var asSubject = ExecuteTemporalQuery(term, ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty);
        try { if (asSubject.MoveNext()) return true; }
        finally { asSubject.Dispose(); }

        var asObject = ExecuteTemporalQuery(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty, term);
        try { return asObject.MoveNext(); }
        finally { asObject.Dispose(); }
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
                        // Binding direction depends on whether the BFS started from the subject
                        // (Case 3) or the object (Case 2):
                        //   Case 3: bind ?subject := _startNode (matches the literal subject),
                        //           bind ?object  := targetNode (the variable receives the walked node)
                        //   Case 2: bind ?subject := targetNode (the variable receives the walked node),
                        //           bind ?object  := _startNode (matches the literal object)
                        var subjectBind = _startedFromObject ? targetNode.AsSpan() : _startNode.AsSpan();
                        var objectBind  = _startedFromObject ? _startNode.AsSpan() : targetNode.AsSpan();
                        if (TryBindVariable(_pattern.Subject, subjectBind, ref bindings) &&
                            TryBindVariable(_pattern.Object, objectBind, ref bindings))
                        {
                            return true;
                        }
                    }
                }
            }
            else
            {
                // For simple paths, use the enumerator. The "discovered node" comes from the
                // OPPOSITE end of the queried triple relative to the BFS start position:
                //   Case 3 (started from subject): we queried `_startNode predicate ?`, so the
                //                                  walked node is triple.Object.
                //   Case 2 (started from object):  we queried `? predicate _startNode`, so the
                //                                  walked node is triple.Subject.
                while (_enumerator.MoveNext())
                {
                    QueryCancellation.ThrowIfCancellationRequested();
                    var triple = _enumerator.Current;
                    var walked = _startedFromObject ? triple.Subject : triple.Object;
                    var targetNode = walked.ToString();

                    if (!_visited!.Contains(targetNode))
                    {
                        _visited.Add(targetNode);
                        _frontier!.Enqueue(targetNode);

                        bindings.TruncateTo(_initialBindingsCount);
                        // Same Case 2 / Case 3 binding split as the grouped branch above.
                        var subjectBind = _startedFromObject ? walked : _startNode.AsSpan();
                        var objectBind  = _startedFromObject ? _startNode.AsSpan() : walked;
                        if (TryBindVariable(_pattern.Subject, subjectBind, ref bindings) &&
                            TryBindVariable(_pattern.Object, objectBind, ref bindings))
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
                    _groupedResults = new Queue<string>(ExecuteGroupedSequenceDirected(_currentNode));
                }
                else
                {
                    var predicate = _pattern.HasPropertyPath
                        ? ResolveTermForQuery(_pattern.Path.Iri)
                        : ResolveTermForQuery(_pattern.Predicate);

                    // BFS expansion direction matches the BFS start orientation: forward when the
                    // walk started from a bound subject (Case 3), inverse when it started from a
                    // bound object (Case 2). Without this flip, the BFS frontier expansion in
                    // Case 2 silently switches direction mid-walk and produces no further results.
                    if (_startedFromObject)
                        _enumerator = ExecuteTemporalQuery(ReadOnlySpan<char>.Empty, predicate, _currentNode.AsSpan());
                    else
                        _enumerator = ExecuteTemporalQuery(_currentNode.AsSpan(), predicate, ReadOnlySpan<char>.Empty);
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
                    _groupedResults = new Queue<string>(ExecuteGroupedSequenceDirected(_startNode));
                }
                else
                {
                    var predicate = _pattern.HasPropertyPath
                        ? ResolveTermForQuery(_pattern.Path.Iri)
                        : ResolveTermForQuery(_pattern.Predicate);

                    if (_startedFromObject)
                        _enumerator = ExecuteTemporalQuery(ReadOnlySpan<char>.Empty, predicate, _startNode.AsSpan());
                    else
                        _enumerator = ExecuteTemporalQuery(_startNode.AsSpan(), predicate, ReadOnlySpan<char>.Empty);
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

        if (_isZeroOrOne || _isGroupedZeroOrOne)
        {
            InitializeZeroOrOne(subject, obj);
            return;
        }

        ReadOnlySpan<char> predicate;
        if (_isInverse)
        {
            // For inverse path, query with swapped subject/object
            predicate = ResolveTermWithStorage(_pattern.Path.Iri, TermPosition.Predicate);
            _enumerator = ExecuteTemporalQuery(obj, predicate, subject);
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
        else
        {
            predicate = ResolveTermWithStorage(_pattern.Predicate, TermPosition.Predicate);
            _enumerator = ExecuteTemporalQuery(subject, predicate, obj);
        }
    }

    private enum TermPosition { Subject, Predicate, Object }

    /// <summary>
    /// Every store read the scan makes: the matches in the graph the pattern reads. A default-graph pattern under a
    /// dataset with FROM reads the merge of the FROM graphs (<see cref="MergedGraphEnumerator"/>), so a path walks the
    /// merge too.
    /// </summary>
    private MergedGraphEnumerator ExecuteTemporalQuery(
        ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        // One read per path, and the merge in a method of its own: each enumerator temporary is ~0.6 KB of this frame,
        // which every scan's deepest call holds (three inlined reads made it 7 KB)
        if (_graph.IsEmpty && _defaultGraphs is { Length: not 1 })
            return QueryMerge(subject, predicate, obj);
        var graph = _graph.IsEmpty && _defaultGraphs != null ? _defaultGraphs[0].AsSpan() : _graph;
        return new MergedGraphEnumerator(QueryGraph(subject, predicate, obj, graph));
    }

    /// <summary>The matches in the merge of two or more FROM graphs, or none (FROM NAMED alone).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private MergedGraphEnumerator QueryMerge(ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj)
    {
        var reads = _defaultGraphs!.Length == 0
            ? Array.Empty<TemporalResultEnumerator>()
            : new TemporalResultEnumerator[_defaultGraphs.Length];
        for (int i = 0; i < reads.Length; i++)
            reads[i] = QueryGraph(subject, predicate, obj, _defaultGraphs[i]);
        return new MergedGraphEnumerator(new GraphMerge(reads, _defaultGraphs, _store,
            new TemporalScope(_temporalMode, _asOfTime, _rangeStart, _rangeEnd)));
    }

    /// <summary>The matches in one graph (empty: the store's default graph).</summary>
    private TemporalResultEnumerator QueryGraph(
        ReadOnlySpan<char> subject, ReadOnlySpan<char> predicate, ReadOnlySpan<char> obj, ReadOnlySpan<char> graph)
    {
        // ADR-024 trigram pre-filter: when the object position is a text:match variable, the caller passes the
        // trigram candidate atom IDs so the scan skips non-candidate objects at the index level (set only for a simple
        // forward pattern, whose ONLY query is this basic scan, so the object is genuinely the object position).
        if (_trigramObjectCandidates != null && _temporalMode == TemporalQueryMode.Current)
            return _store.QueryCurrentWithCandidates(subject, predicate, obj, _trigramObjectCandidates, graph);

        return new TemporalScope(_temporalMode, _asOfTime, _rangeStart, _rangeEnd).Query(_store, subject, predicate, obj, graph);
    }

    /// <summary>
    /// Executes a grouped sequence path from a given start node.
    /// For example, for (p1/p2/p3)*, this executes the full sequence p1→p2→p3
    /// and returns all nodes reachable at the end of the sequence.
    /// </summary>
    /// <summary>
    /// Walks the grouped sequence in the direction implied by the path AST.
    /// For Grouped{ZeroOrMore,OneOrMore,ZeroOrOne} with <see cref="PropertyPath.IsInverseGroup"/> set
    /// (e.g., from parsing ^(P)*, ^(A|B)+, ^iri?), each step traverses the inner content backward.
    /// Otherwise, walks forward via <see cref="ExecuteGroupedSequence"/>.
    /// </summary>
    private List<string> ExecuteGroupedSequenceDirected(string startNode)
    {
        // Route through the unified walker: handles paren-wrapped content, alternatives,
        // sequences, per-predicate '^', and IsInverseGroup uniformly. Returns a List<string>
        // to preserve the existing call-site contract (Queue construction in callers); the
        // walker fills a temporary queue, which we drain into a list once.
        var pooled = RentTempQueue();
        WalkGroupedContentInto(startNode, pooled);
        var results = new List<string>(pooled.Count);
        while (pooled.Count > 0) results.Add(pooled.Dequeue());
        ReturnTempQueue(pooled);
        return results;
    }

    // Legacy methods ExecuteGroupedSequence + ExecuteInverseGroupedSequence have been retired —
    // replaced by the unified WalkGroupedContentInto walker (below) which handles forward, inverse,
    // alternative, sequence, and paren-wrapped content uniformly under zero-GC discipline.

    /// <summary>
    /// Walk one application of the grouped path content from <paramref name="startNode"/> and emit
    /// reached nodes into <paramref name="output"/>. A single walker handles forward and inverse
    /// via the <see cref="PropertyPath.IsInverseGroup"/> flag and per-predicate <c>^</c> markers.
    /// </summary>
    /// <remarks>
    /// Content shapes handled:
    ///   <c>P</c>           — single forward predicate
    ///   <c>^P</c>          — single inverse predicate
    ///   <c>P1/P2/...</c>   — sequence of predicates (each may be <c>^</c>-prefixed)
    ///   <c>P1|P2|...</c>   — top-level alternative (each branch may itself be a sub-expression)
    ///   <c>(X)</c>         — parenthesised sub-expression (recursively walked)
    /// Operator depth is tracked across both <c>&lt;&gt;</c> (IRI brackets) and <c>()</c>
    /// (group nesting) so nested groups don't trigger spurious splits.
    ///
    /// Zero-GC discipline:
    ///   * No <c>contentStr.ToString()</c>; all parsing operates on spans into <c>_source</c>.
    ///   * Predicate / branch range tables live in <c>stackalloc int[]</c> on the walker stack.
    ///   * Frontier sets reuse <c>_walkerCurrent</c> / <c>_walkerNext</c> fields between calls.
    ///   * <c>HashSet&lt;string&gt;.GetAlternateLookup&lt;ReadOnlySpan&lt;char&gt;&gt;()</c> avoids
    ///     <c>ToString()</c> per match — strings are materialised only when a node is genuinely
    ///     new in this step's frontier.
    /// </remarks>
    private void WalkGroupedContentInto(string startNode, Queue<string> output)
    {
        var content = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);
        // Inverse direction applies when:
        //   * the parser flagged a quantified group as inverse (e.g., from ^(P)*, ^((A|B))+); or
        //   * the BFS started from the bound OBJECT position (Case 2) — semantically requires
        //     walking the path's reverse direction from the start node to find subjects that
        //     reach the bound object via the original (un-flipped) path.
        bool inverse = _pattern.Path.IsInverseGroup;
        if (_startedFromObject) inverse = !inverse;
        WalkPathContentInto(startNode, content, inverse, output);
    }

    /// <summary>
    /// Recursive worker for <see cref="WalkGroupedContentInto"/>. Strips outer balanced parens,
    /// detects the top-level operator, and dispatches to sequence/alternative/single-predicate handling.
    /// </summary>
    private void WalkPathContentInto(string startNode, ReadOnlySpan<char> content, bool inverseDir, Queue<string> output)
    {
        content = TrimOuterParens(content.Trim());

        Span<int> ranges = stackalloc int[32]; // up to 16 segments (start + length each) — comfortable for real queries
        char topOp = ScanTopLevelPathOperator(content, ranges, out int rangeCount);

        // Re-entrancy: this walker recurses (alternation-of-sequences, nested alternatives, sequences-of-groups), so
        // each level must use its OWN dedup set — the previous shared _walkerStepSeen was clobbered across levels (a
        // branch's final node ended up "already seen" by an inner leg's dedup and was dropped → empty result, ADR-047).
        if (topOp == '|')
        {
            // Top-level alternative: union over branches from startNode.
            // (Inverse marker on the outer group flips direction for each branch.)
            var stepSeen = new HashSet<string>();
            for (int i = 0; i < rangeCount; i++)
            {
                var branch = content.Slice(ranges[i * 2], ranges[i * 2 + 1]);
                WalkOneBranchInto(startNode, branch, inverseDir, output, stepSeen);
            }
        }
        else if (topOp == '/')
        {
            // Top-level sequence: chain through each leg.
            // For inverse direction, walk legs in REVERSE order with each leg also inverse-flipped.
            WalkSequenceInto(startNode, content, ranges, rangeCount, inverseDir, output);
        }
        else
        {
            // Single predicate, possibly with leading '^'.
            WalkOneBranchInto(startNode, content, inverseDir, output, new HashSet<string>());
        }
    }

    /// <summary>
    /// Walk a single branch (which may itself be a sub-expression) from a start node, emitting
    /// each unique target into <paramref name="output"/>. Uses <see cref="HashSet{T}.AlternateLookup{TAlt}"/>
    /// to dedupe by span before allocating a string.
    /// </summary>
    private void WalkOneBranchInto(string startNode, ReadOnlySpan<char> branch, bool inverseDir,
        Queue<string> output, HashSet<string> stepSeen)
    {
        branch = TrimOuterParens(branch.Trim());

        // If the branch is itself a composite (sequence or alternative), recurse.
        Span<int> innerRanges = stackalloc int[32];
        char innerOp = ScanTopLevelPathOperator(branch, innerRanges, out int innerCount);
        if (innerOp == '/' || innerOp == '|')
        {
            // Recurse into a fresh per-recursion frontier — but we still need to dedupe
            // emissions into the caller's stepSeen set. Use a temporary intermediate queue.
            var pooled = RentTempQueue();
            WalkPathContentInto(startNode, branch, inverseDir, pooled);
            while (pooled.Count > 0)
            {
                var node = pooled.Dequeue();
                if (stepSeen.Add(node))
                    output.Enqueue(node);
            }
            ReturnTempQueue(pooled);
            return;
        }

        // A trailing quantifier (X*, X+, X?) binds TIGHTER than / and |, so it only reaches here once no top-level
        // sequence/alternative remains (e.g. the leg p* of p/p*, or the branch r* of p*|r*). Evaluate it as a closure
        // from startNode, walking the base X per hop (recursively, so (a/b)* and (a|b)+ work too). ADR-047.
        if (branch.Length >= 2 && (branch[^1] == '*' || branch[^1] == '+' || branch[^1] == '?'))
        {
            WalkQuantifiedInto(startNode, branch.Slice(0, branch.Length - 1).Trim(), branch[^1], inverseDir, output, stepSeen);
            return;
        }

        // Atomic predicate: optional leading '^', then the IRI/prefixed name.
        bool branchIsInverse = branch.Length > 0 && branch[0] == '^';
        var predicateSpan = branchIsInverse ? branch.Slice(1).TrimStart() : branch;
        var predicate = ExpandPrefixedName(predicateSpan);

        // Effective direction: outer inverse XOR per-branch inverse marker.
        bool effectiveInverse = inverseDir ^ branchIsInverse;

        // Query in the effective direction. For inverse traversal from startNode, query with
        // startNode as object position and accumulate subjects.
        MergedGraphEnumerator enumerator;
        if (effectiveInverse)
            enumerator = ExecuteTemporalQuery(ReadOnlySpan<char>.Empty, predicate, startNode.AsSpan());
        else
            enumerator = ExecuteTemporalQuery(startNode.AsSpan(), predicate, ReadOnlySpan<char>.Empty);

        try
        {
            var seenLookup = stepSeen.GetAlternateLookup<ReadOnlySpan<char>>();
            while (enumerator.MoveNext())
            {
                QueryCancellation.ThrowIfCancellationRequested();
                var triple = enumerator.Current;
                var target = effectiveInverse ? triple.Subject : triple.Object;

                // Dedupe by span first; only ToString() if genuinely new.
                if (!seenLookup.Contains(target))
                {
                    var s = target.ToString();
                    stepSeen.Add(s);
                    output.Enqueue(s);
                }
            }
        }
        finally
        {
            enumerator.Dispose();
        }
    }

    /// <summary>
    /// Walk a sequence (legs separated by top-level <c>/</c>). Each leg may be inverse-prefixed
    /// or a sub-group. The outer <paramref name="inverseDir"/> flag reverses leg order and
    /// inverts each leg's direction (semantics of inverse-of-sequence).
    /// </summary>
    /// <summary>
    /// ADR-047 — evaluate a quantified path element (X*, X+, X?) from <paramref name="startNode"/> as a closure,
    /// walking the base expression <paramref name="baseExpr"/> per hop (recursively via WalkOneBranchInto, so X may
    /// itself be a sequence/alternative/group). '*' and '?' include the reflexive (zero-length) node; '+' includes
    /// startNode only if a cycle reaches it. Path set semantics (distinct nodes).
    /// </summary>
    private void WalkQuantifiedInto(string startNode, ReadOnlySpan<char> baseExpr, char quant, bool inverseDir,
        Queue<string> output, HashSet<string> stepSeen)
    {
        var result = new HashSet<string>();
        if (quant == '*' || quant == '?')
            result.Add(startNode); // zero-length (reflexive)

        if (quant == '?')
        {
            // Zero-or-one: at most one hop.
            var pooled = RentTempQueue();
            WalkOneBranchInto(startNode, baseExpr, inverseDir, pooled, new HashSet<string>());
            while (pooled.Count > 0) result.Add(pooled.Dequeue());
            ReturnTempQueue(pooled);
        }
        else
        {
            // Zero/one-or-more: BFS transitive closure. `visited` bounds the frontier (cycle-safe); `result`
            // accumulates every node reached via >= 1 hop, so a cycle back to startNode is included (as '+' requires).
            var frontier = new HashSet<string> { startNode };
            var visited = new HashSet<string> { startNode };
            while (frontier.Count > 0)
            {
                var nextFrontier = new HashSet<string>();
                foreach (var node in frontier)
                {
                    var pooled = RentTempQueue();
                    WalkOneBranchInto(node, baseExpr, inverseDir, pooled, new HashSet<string>());
                    while (pooled.Count > 0)
                    {
                        var reached = pooled.Dequeue();
                        result.Add(reached);
                        if (visited.Add(reached)) nextFrontier.Add(reached);
                    }
                    ReturnTempQueue(pooled);
                }
                frontier = nextFrontier;
            }
        }

        foreach (var node in result)
            if (stepSeen.Add(node))
                output.Enqueue(node);
    }

    private void WalkSequenceInto(string startNode, ReadOnlySpan<char> content, scoped ReadOnlySpan<int> ranges,
        int rangeCount, bool inverseDir, Queue<string> output)
    {
        // Local frontier + per-leg dedup sets (NOT the shared instance fields) so nested/alternating sequences are
        // re-entrant — see the note in WalkPathContentInto. Frontier is a set: a node reached by multiple paths is
        // carried forward once (path set semantics).
        var current = new HashSet<string> { startNode };
        var next = new HashSet<string>();
        var legStepSeen = new HashSet<string>();

        // Iterate legs in source order for forward, reverse order for inverse-of-sequence.
        // For each leg, take all current frontier nodes through the leg (forward or inverse-flipped).
        for (int idx = 0; idx < rangeCount; idx++)
        {
            int legIndex = inverseDir ? (rangeCount - 1 - idx) : idx;
            var leg = content.Slice(ranges[legIndex * 2], ranges[legIndex * 2 + 1]);

            legStepSeen.Clear();
            foreach (var node in current)
            {
                // Walk this leg from `node` into a temp queue, then merge into `next`.
                var pooled = RentTempQueue();
                WalkOneBranchInto(node, leg, inverseDir, pooled, legStepSeen);
                while (pooled.Count > 0)
                    next.Add(pooled.Dequeue());
                ReturnTempQueue(pooled);
            }

            // Swap roles for next iteration.
            (current, next) = (next, current);
            next.Clear();

            if (current.Count == 0)
                break;
        }

        // Emit the final frontier into the output queue.
        foreach (var node in current)
            output.Enqueue(node);
    }

    /// <summary>
    /// Strip a single layer of outer balanced parentheses if they wrap the entire expression
    /// (e.g., <c>(^A/B)</c> → <c>^A/B</c>). Leaves nested patterns like <c>A/(B|C)</c> alone.
    /// </summary>
    private static ReadOnlySpan<char> TrimOuterParens(ReadOnlySpan<char> s)
    {
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            int depth = 0;
            bool wraps = true;
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0 && i != s.Length - 1) { wraps = false; break; }
                }
            }
            if (!wraps || depth != 0) break;
            s = s.Slice(1, s.Length - 2).Trim();
        }
        return s;
    }

    /// <summary>
    /// Scan for top-level <c>/</c> (sequence) or <c>|</c> (alternative) splits in <paramref name="content"/>,
    /// honoring depth across both IRI brackets <c>&lt;&gt;</c> and group parens <c>()</c>.
    /// Per SPARQL precedence, <c>|</c> binds looser than <c>/</c> — so if any top-level <c>|</c> exists,
    /// it determines the split shape and <c>/</c> within branches is recurse-resolved.
    /// </summary>
    /// <returns><c>'|'</c> if alternative split; <c>'/'</c> if sequence split; <c>'\0'</c> if neither (single predicate).</returns>
    private static char ScanTopLevelPathOperator(ReadOnlySpan<char> content, Span<int> ranges, out int rangeCount)
    {
        // First pass: detect top-level '|'. If any, split on '|' (alternative).
        // Otherwise: detect top-level '/'. If any, split on '/' (sequence).
        // Otherwise: single predicate.
        char op = ScanTopLevelOp(content, '|');
        if (op == '|')
        {
            rangeCount = SplitTopLevel(content, '|', ranges);
            return '|';
        }
        op = ScanTopLevelOp(content, '/');
        if (op == '/')
        {
            rangeCount = SplitTopLevel(content, '/', ranges);
            return '/';
        }
        rangeCount = 0;
        return '\0';
    }

    private static char ScanTopLevelOp(ReadOnlySpan<char> content, char target)
    {
        int iriDepth = 0, parenDepth = 0;
        for (int i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '<') iriDepth++;
            else if (c == '>') iriDepth--;
            else if (iriDepth == 0)
            {
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                else if (parenDepth == 0 && c == target) return target;
            }
        }
        return '\0';
    }

    private static int SplitTopLevel(ReadOnlySpan<char> content, char sep, Span<int> ranges)
    {
        int count = 0, segmentStart = 0;
        int iriDepth = 0, parenDepth = 0;
        for (int i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '<') iriDepth++;
            else if (c == '>') iriDepth--;
            else if (iriDepth == 0)
            {
                if (c == '(') parenDepth++;
                else if (c == ')') parenDepth--;
                else if (parenDepth == 0 && c == sep)
                {
                    if (count * 2 + 1 >= ranges.Length) break; // overflow guard
                    ranges[count * 2] = segmentStart;
                    ranges[count * 2 + 1] = i - segmentStart;
                    count++;
                    segmentStart = i + 1;
                }
            }
        }
        // Final segment
        if (count * 2 + 1 < ranges.Length && segmentStart < content.Length)
        {
            ranges[count * 2] = segmentStart;
            ranges[count * 2 + 1] = content.Length - segmentStart;
            count++;
        }
        return count;
    }

    private void EnsureWalkerStepSeen()
    {
        _walkerStepSeen ??= new HashSet<string>();
    }

    private void EnsureFrontierSets()
    {
        _walkerCurrent ??= new HashSet<string>();
        _walkerNext ??= new HashSet<string>();
    }

    // Tiny pool of one Queue<string> for nested recursion. Most queries don't recurse deeply,
    // and the queue is reused across the BFS frontier walks rather than re-allocated.
    private Queue<string>? _walkerTempQueue;
    private bool _walkerTempQueueInUse;
    private Queue<string> RentTempQueue()
    {
        if (_walkerTempQueue is not null && !_walkerTempQueueInUse)
        {
            _walkerTempQueueInUse = true;
            _walkerTempQueue.Clear();
            return _walkerTempQueue;
        }
        // Recursion deeper than one level — fall back to a fresh queue.
        // Real WDBench property-path content depths max out at ~3, so this is cold.
        return new Queue<string>();
    }

    private void ReturnTempQueue(Queue<string> q)
    {
        if (_walkerTempQueue is null)
        {
            _walkerTempQueue = q;
            _walkerTempQueueInUse = false;
            return;
        }
        if (ReferenceEquals(q, _walkerTempQueue))
        {
            _walkerTempQueueInUse = false;
        }
        // Else: a fresh queue used by a deeper recursion frame; let GC reclaim.
    }

    /// <summary>
    /// Discovers all starting nodes that can begin one application of the grouped path content.
    /// Walks the content's first leg (or every branch, for top-level alternatives) and queries
    /// the underlying predicate, accumulating subjects (forward) or objects (inverse) as candidate
    /// starting nodes. Reflexive emissions for zero-or-more enrich <c>allNodes</c> with the full
    /// node set discovered while scanning.
    /// </summary>
    /// <remarks>
    /// Replaces the prior implementation which split the content on <c>/</c> with only IRI-bracket
    /// depth tracking — that produced bogus first-predicate slices for paren-wrapped or alternative
    /// content. The walker-aware version honors paren depth, top-level alternatives, and per-leg
    /// <c>^</c> prefixes uniformly with <see cref="WalkPathContentInto"/>.
    /// </remarks>
    private (HashSet<string> subjects, HashSet<string> allNodes) DiscoverGroupedSequenceStartNodes()
    {
        var subjects = new HashSet<string>();
        var allNodes = new HashSet<string>();

        var content = _source.Slice(_pattern.Path.LeftStart, _pattern.Path.LeftLength);
        bool outerInverse = _pattern.Path.IsInverseGroup;
        DiscoverContentStartNodes(content, outerInverse, subjects, allNodes);

        // For zero-or-more, reflexive bindings include ALL nodes in the graph (per SPARQL 1.1
        // semantics), not just those reached via the predicate. Query unrestricted triples
        // and accumulate. Skipped for one-or-more / zero-or-one — those don't have a reflexive
        // emission contract for unconnected nodes.
        if (_isGroupedZeroOrMore)
        {
            var allTriplesEnumerator = ExecuteTemporalQuery(
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty,
                ReadOnlySpan<char>.Empty);
            try
            {
                var lookup = allNodes.GetAlternateLookup<ReadOnlySpan<char>>();
                while (allTriplesEnumerator.MoveNext())
                {
                    QueryCancellation.ThrowIfCancellationRequested();
                    var t = allTriplesEnumerator.Current;
                    if (!lookup.Contains(t.Subject)) allNodes.Add(t.Subject.ToString());
                    if (!lookup.Contains(t.Object)) allNodes.Add(t.Object.ToString());
                }
            }
            finally
            {
                allTriplesEnumerator.Dispose();
            }
        }

        return (subjects, allNodes);
    }

    /// <summary>
    /// Recursively descend into <paramref name="content"/> to find candidate starting nodes:
    /// for an alternative, every branch contributes; for a sequence, only the first leg in walk
    /// order; for an atomic predicate, query the predicate and accumulate subjects (or objects,
    /// when the effective direction is inverse).
    /// </summary>
    private void DiscoverContentStartNodes(ReadOnlySpan<char> content, bool outerInverse,
        HashSet<string> subjects, HashSet<string> allNodes)
    {
        content = TrimOuterParens(content.Trim());

        Span<int> ranges = stackalloc int[32];
        char topOp = ScanTopLevelPathOperator(content, ranges, out int rangeCount);

        if (topOp == '|')
        {
            for (int i = 0; i < rangeCount; i++)
                DiscoverContentStartNodes(content.Slice(ranges[i * 2], ranges[i * 2 + 1]),
                    outerInverse, subjects, allNodes);
            return;
        }

        if (topOp == '/')
        {
            // For inverse-of-sequence, the leg walked first is the LAST in source order.
            int firstLegIdx = outerInverse ? (rangeCount - 1) : 0;
            DiscoverContentStartNodes(
                content.Slice(ranges[firstLegIdx * 2], ranges[firstLegIdx * 2 + 1]),
                outerInverse, subjects, allNodes);
            return;
        }

        // Atomic: optional '^' prefix then a predicate.
        bool legIsInverse = content.Length > 0 && content[0] == '^';
        var predSpan = legIsInverse ? content.Slice(1).TrimStart() : content;
        var predicate = ExpandPrefixedName(predSpan);
        bool effectiveInverse = outerInverse ^ legIsInverse;

        var enumerator = ExecuteTemporalQuery(
            ReadOnlySpan<char>.Empty,
            predicate,
            ReadOnlySpan<char>.Empty);
        try
        {
            var subjLookup = subjects.GetAlternateLookup<ReadOnlySpan<char>>();
            var nodeLookup = allNodes.GetAlternateLookup<ReadOnlySpan<char>>();
            while (enumerator.MoveNext())
            {
                QueryCancellation.ThrowIfCancellationRequested();
                var t = enumerator.Current;
                var startCandidate = effectiveInverse ? t.Object : t.Subject;
                if (!subjLookup.Contains(startCandidate)) subjects.Add(startCandidate.ToString());
                if (!nodeLookup.Contains(t.Subject)) allNodes.Add(t.Subject.ToString());
                if (!nodeLookup.Contains(t.Object)) allNodes.Add(t.Object.ToString());
            }
        }
        finally
        {
            enumerator.Dispose();
        }
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
                    QueryCancellation.ThrowIfCancellationRequested();
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
                        QueryCancellation.ThrowIfCancellationRequested();
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
                    _groupedResults = new Queue<string>(ExecuteGroupedSequenceDirected(_startNode));
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
            _startedFromObject = true;

            _visited.Add(_startNode);
            _currentNode = _startNode;

            if (isGrouped)
            {
                _groupedResults = new Queue<string>(ExecuteGroupedSequenceDirected(_startNode));
            }
            else
            {
                // Object-bound transitive walk: query INVERSE direction from the bound object
                // to find subjects whose forward path reaches the object.
                // For `?x P+ <obj>`, this is "ancestors of obj" — `<obj>` as object position,
                // accumulating subjects via triple.Subject in MoveNextTransitive.
                _enumerator = ExecuteTemporalQuery(
                    ReadOnlySpan<char>.Empty,
                    predicate,
                    _startNode.AsSpan());
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
                _groupedResults = new Queue<string>(ExecuteGroupedSequenceDirected(_startNode));
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
    /// The matching form of a bound variable. A numeric or boolean binding (e.g. a BIND result) carries a BARE lexical
    /// ("2") plus its datatype tag (ADR-047: MaterializedRow preserves the tag), so to match a stored typed literal
    /// ("2"^^xsd:integer) the scan needs the canonical typed form. A triple-bound value is already a full term
    /// (Type=String) and is returned verbatim — so this never double-types one.
    /// </summary>
    private ReadOnlySpan<char> CanonicalBoundValue(int idx)
    {
        var value = _initialBindings.GetString(idx);
        string? datatype = _initialBindings.GetType(idx) switch
        {
            BindingValueType.Integer => "integer",
            BindingValueType.Double => "double",
            BindingValueType.Boolean => "boolean",
            _ => null,
        };
        if (datatype == null) return value;
        _literalScratch = $"\"{value}\"^^<http://www.w3.org/2001/XMLSchema#{datatype}>";
        return _literalScratch.AsSpan();
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
        if (SyntheticTerms.IsSynthetic(term.Start))
        {
            if (term.IsVariable)
            {
                var varName = SyntheticTerms.GetSyntheticVarName(term.Start);
                var idx = _initialBindings.FindBinding(varName);
                if (idx >= 0)
                    return _initialBindings.GetString(idx);
                return ReadOnlySpan<char>.Empty;
            }
            return SyntheticTerms.GetSyntheticIri(term.Start);
        }

        if (term.IsVariable)
        {
            var varName = _source.Slice(term.Start, term.Length);
            var idx = _initialBindings.FindBinding(varName);
            if (idx >= 0)
                return CanonicalBoundValue(idx);
            return ReadOnlySpan<char>.Empty;
        }

        if (term.IsBlankNode)
        {
            // A WHERE's labeled blank node (_:name) reaches the scan as a variable (SPARQL 1.1 §4.1.4; the pattern
            // tree adds it as one), so a label here is from a caller without a tree and matches as written.
            // Anonymous blank nodes ([]) act as wildcards
            var bnSpan = _source.Slice(term.Start, term.Length);
            if (bnSpan.Length >= 2 && bnSpan[0] == '_' && bnSpan[1] == ':')
                return bnSpan; // Return the actual blank node label
            return ReadOnlySpan<char>.Empty; // Anonymous blank node - wildcard
        }

        var termSpan = _source.Slice(term.Start, term.Length);

        // 'a' keyword + prefixed-name expansion (shared PrefixExpander — THROWS on an undefined prefix per
        // W3C SPARQL 1.1 §4.1.3 instead of silently returning the unexpanded token). Null = not a prefixed
        // name; fall through to literal/numeric canonicalization. Position-specific scratch holds the IRI.
        var expanded = PrefixExpander.TryExpand(termSpan, _prefixes, _source);
        if (expanded != null)
        {
            switch (position)
            {
                case TermPosition.Subject: _expandedSubject = expanded; return _expandedSubject.AsSpan();
                case TermPosition.Predicate: _expandedPredicate = expanded; return _expandedPredicate.AsSpan();
                default: _expandedObject = expanded; return _expandedObject.AsSpan();
            }
        }

        // A literal, number or boolean constant matches the term it denotes, in its stored form
        // (LiteralForm.StoredTerm): a long string matches its short form, 30 matches "30"^^xsd:integer
        return LiteralForm.StoredTerm(termSpan, _prefixes, _source, out _literalScratch);
    }

    /// <summary>
    /// Simple term resolution for single-term use cases (transitive paths).
    /// Uses shared buffer since only one term is resolved at a time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ReadOnlySpan<char> ResolveTermForQuery(Term term)
    {
        if (SyntheticTerms.IsSynthetic(term.Start))
        {
            if (term.IsVariable)
            {
                var varName = SyntheticTerms.GetSyntheticVarName(term.Start);
                var idx = _initialBindings.FindBinding(varName);
                if (idx >= 0)
                    return _initialBindings.GetString(idx);
                return ReadOnlySpan<char>.Empty;
            }
            return SyntheticTerms.GetSyntheticIri(term.Start);
        }

        if (term.IsVariable)
        {
            var varName = _source.Slice(term.Start, term.Length);
            var idx = _initialBindings.FindBinding(varName);
            if (idx >= 0)
                return CanonicalBoundValue(idx);
            return ReadOnlySpan<char>.Empty;
        }

        if (term.IsBlankNode)
        {
            // A WHERE's labeled blank node (_:name) reaches the scan as a variable (SPARQL 1.1 §4.1.4; the pattern
            // tree adds it as one), so a label here is from a caller without a tree and matches as written.
            // Anonymous blank nodes ([]) act as wildcards
            var bnSpan = _source.Slice(term.Start, term.Length);
            if (bnSpan.Length >= 2 && bnSpan[0] == '_' && bnSpan[1] == ':')
                return bnSpan; // Return the actual blank node label
            return ReadOnlySpan<char>.Empty; // Anonymous blank node - wildcard
        }

        var termSpan = _source.Slice(term.Start, term.Length);

        // 'a' keyword + prefixed-name expansion (shared PrefixExpander — THROWS on an undefined prefix per
        // W3C SPARQL 1.1 §4.1.3). Null = not a prefixed name; fall through to literal/numeric canonicalization.
        var expanded = PrefixExpander.TryExpand(termSpan, _prefixes, _source);
        if (expanded != null)
        {
            _expandedSubject = expanded; // single-term resolution reuses the subject scratch
            return _expandedSubject.AsSpan();
        }

        // A literal, number or boolean constant matches the term it denotes, in its stored form
        // (LiteralForm.StoredTerm): a long string matches its short form, 30 matches "30"^^xsd:integer
        return LiteralForm.StoredTerm(termSpan, _prefixes, _source, out _literalScratch);
    }

    /// <summary>
    /// Expand a path predicate (from raw start/length offsets) with prefix expansion.
    /// Used for Alternative path predicates which are stored as offsets rather than Term structs.
    /// </summary>
    private ReadOnlySpan<char> ExpandPathPredicate(int start, int length)
    {
        var predSpan = _source.Slice(start, length);
        var expanded = PrefixExpander.TryExpand(predSpan, _prefixes, _source); // 'a' / prefixed name; throws on undefined
        if (expanded != null) { _expandedPredicate = expanded; return _expandedPredicate.AsSpan(); }
        return predSpan;
    }

    /// <summary>
    /// Expand a path predicate from a span with prefix expansion.
    /// Used for path segments extracted during n-ary alternative processing.
    /// </summary>
    private ReadOnlySpan<char> ExpandPathPredicateSpan(ReadOnlySpan<char> predSpan)
    {
        var expanded = PrefixExpander.TryExpand(predSpan, _prefixes, _source); // 'a' / prefixed name; throws on undefined
        if (expanded != null) { _expandedPredicate = expanded; return _expandedPredicate.AsSpan(); }
        return predSpan;
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
                expandedCurrent = SyntheticTerms.RdfType.AsSpan();

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
                expandedInner = SyntheticTerms.RdfType.AsSpan();
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
        var expanded = PrefixExpander.TryExpand(term, _prefixes, _source);
        if (expanded is null) return term;
        _expandedPredicate = expanded;
        return _expandedPredicate.AsSpan();
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
        if (SyntheticTerms.IsSynthetic(term.Start))
            varName = SyntheticTerms.GetSyntheticVarName(term.Start);
        else
            varName = _source.Slice(term.Start, term.Length);

        // Check if already bound (from earlier pattern in join)
        var existingIndex = bindings.FindBinding(varName);
        if (existingIndex >= 0)
        {
            // Must match existing binding
            var existingValue = bindings.GetString(existingIndex);
            if (value.SequenceEqual(existingValue))
                return true;

            // ADR-047: a numeric/boolean binding (e.g. a BIND result) stores a BARE lexical ("2") plus its datatype tag,
            // while the matched term is the full typed literal ("2"^^xsd:integer). Compare value-aware — the typed
            // literal's lexical part against the bare existing value — so BIND(?o+1 AS ?z) … ?p ?z joins (W3C bind03).
            if (bindings.GetType(existingIndex) is BindingValueType.Integer or BindingValueType.Double or BindingValueType.Boolean
                && value.Length > 1 && value[0] == '"')
            {
                int closeQuote = value.LastIndexOf('"');
                if (closeQuote > 0)
                    return value.Slice(1, closeQuote - 1).SequenceEqual(existingValue);
            }
            return false;
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
