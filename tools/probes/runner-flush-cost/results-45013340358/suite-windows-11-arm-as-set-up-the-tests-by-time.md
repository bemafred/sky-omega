### Windows arm64: as the runner is set up

- Windows arm64: as the runner is set up
- RealTimeProtectionEnabled=True
- GetTempPath=C:\Users\runneradmin\AppData\Local\Temp\
- Mercury.Tests: 2288 s, exit code 2

6730 tests, 400 not passed. First start to last end: 2285 s. The tests' durations add up to 8657 s.

| Test class | Tests | Their durations, s | Share |
|---|--:|--:|--:|
| Infrastructure.AllocationTests | 7 | 875.6 | 10 % |
| W3C.SparqlConformanceTests | 997 | 820.1 | 9 % |
| Sparql.LoadExecutorStressTests | 15 | 814.2 | 9 % |
| Storage.PruningTests | 16 | 661.3 | 8 % |
| Storage.IndexFileTests | 7 | 558.1 | 6 % |
| Storage.CrashRecoveryTests | 10 | 546.6 | 6 % |
| Infrastructure.ConcurrentAccessTests | 15 | 399.6 | 5 % |
| Storage.QuadIndexCursorTests | 7 | 375.4 | 4 % |
| Storage.AtomStoreTests | 46 | 364.4 | 4 % |
| Storage.TemporalQuadIndexTests | 32 | 364.3 | 4 % |
| Sparql.NowTests | 14 | 338.6 | 4 % |
| Rdf.StreamingIoTests | 15 | 248.2 | 3 % |
| Storage.ReferenceQuadIndexTests | 16 | 246.2 | 3 % |
| Sparql.QueryExecutorTests | 231 | 203.2 | 2 % |
| Storage.BulkLoadTests | 7 | 101.7 | 1 % |
| Sparql.SparqlTextSearchTests | 14 | 101.1 | 1 % |
| Sparql.PropertyPathRegressionTests | 3 | 95.0 | 1 % |
| Sparql.GraphTreeEvaluatorTests | 33 | 74.8 | 1 % |
| Sparql.SparqlHttpServerTests | 34 | 74.0 | 1 % |
| Storage.QuadStoreTests | 51 | 68.5 | 1 % |
| Storage.DeleteEndsValidTimeTests | 16 | 64.8 | 1 % |
| Diagnostics.MetricEmissionThrottleTests | 7 | 55.1 | 1 % |
| Storage.MinimalQuadIndexTests | 10 | 52.4 | 1 % |
| Sparql.TreeJoinExecutorTests | 27 | 51.6 | 1 % |
| Diagnostics.AtomStoreMetricsTests | 6 | 42.9 | 0 % |
| Sparql.LongExpansionTests | 7 | 40.8 | 0 % |
| Infrastructure.BufferManagerTests | 20 | 38.7 | 0 % |
| Storage.MemoryTimeWriteRuleTests | 38 | 34.5 | 0 % |
| Sparql.MemoryWordsTests | 48 | 32.5 | 0 % |
| Repl.ReplSessionTests | 59 | 31.3 | 0 % |

| Test | Class | Duration, s |
|---|---|--:|
| GraphPath Limit PushesIntoScan | Infrastructure.AllocationTests | 602.6 |
| LargeTransfer BatchesCorrectly | Storage.PruningTests | 590.2 |
| Stress ZeroLimits ShouldLoadEverything | Sparql.LoadExecutorStressTests | 583.8 |
| ManySessions EachKilled KeepEveryBatch | Storage.CrashRecoveryTests | 523.7 |
| ReadersOfAStoreAtTheSameTime FindEveryQuad | Storage.IndexFileTests | 461.3 |
| TimeFirst TemporalRangeQuery VisitsFewerPages | Storage.TemporalQuadIndexTests | 321.5 |
| GraphPath HotScanLoop IsZeroGcPerScanStep | Infrastructure.AllocationTests | 257.3 |
| Aggregate OverStoredTriples ReadsOneValue | Sparql.NowTests | 195.2 |
| Intern HighCollisionLoad HandlesGracefully | Storage.AtomStoreTests | 175.7 |
| StressTest HighConcurrency NoExceptions | Infrastructure.ConcurrentAccessTests | 167.8 |
| Stress ConcurrentLoads ShouldNotCorrupt | Sparql.LoadExecutorStressTests | 122.9 |
| LoadFileAsync ChunkedCommits AllTriplesQueryable | Rdf.StreamingIoTests | 110.6 |
| AppendSorted LargeMonotonicRun ExceedsLeafAndPromotes | Storage.ReferenceQuadIndexTests | 109.3 |
| GetAtomId AfterManyInserts StillFindsEntries | Storage.AtomStoreTests | 107.0 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: TimeFirst) | Storage.QuadIndexCursorTests | 92.9 |
| Stress RapidSuccessiveLoads ShouldNotLeakResources | Sparql.LoadExecutorStressTests | 92.3 |
| TransitivePath HonorsCancellationToken | Sparql.PropertyPathRegressionTests | 91.8 |
| Add ManyInSortedOrder PreservesUniqueness | Storage.ReferenceQuadIndexTests | 81.5 |
| ConcurrentWrites MultipleThreads AllSucceed | Infrastructure.ConcurrentAccessTests | 81.3 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: EntityFirst) | Storage.QuadIndexCursorTests | 78.9 |
| Update OnEveryRow ReadsOneValue | Sparql.NowTests | 73.5 |
| TheGraphIndexsCursor GivesEveryEntryItsQuerySelects WithItsVersion InKeyOrder | Storage.QuadIndexCursorTests | 71.7 |
| UpdateRequest EveryOperation ReadsOneValue | Sparql.NowTests | 67.7 |
| LoadFileAsync ProgressReporting FiresAtChunkBoundaries | Rdf.StreamingIoTests | 58.1 |
| ConcurrentCallers ExactlyOneReturnsTruePerInterval | Diagnostics.MetricEmissionThrottleTests | 55.0 |
| TextMatch TrigramPreFiltering ReducesEvaluations | Sparql.SparqlTextSearchTests | 53.6 |
| BulkLoad ChunkedBatches AllTriplesQueryable | Storage.BulkLoadTests | 51.6 |
| TheMinimalIndexsCursor GivesEveryEntryItsQuerySelects InKeyOrder | Storage.QuadIndexCursorTests | 51.5 |
| MoreFactsThanALeafHolds PageSplitsAndAllQueryable | Storage.MinimalQuadIndexTests | 51.4 |
| DeleteCurrent PeriodsSpanningLeafPages FindsTheOneThatHolds | Storage.DeleteEndsValidTimeTests | 48.4 |

