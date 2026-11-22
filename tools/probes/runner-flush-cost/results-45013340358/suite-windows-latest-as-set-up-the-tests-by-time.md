### Windows x64: as the runner is set up

- Windows x64: as the runner is set up
- RealTimeProtectionEnabled=False
- GetTempPath=C:\Users\runneradmin\AppData\Local\Temp\
- Mercury.Tests: 2256 s, exit code 2

6730 tests, 400 not passed. First start to last end: 2253 s. The tests' durations add up to 8105 s.

| Test class | Tests | Their durations, s | Share |
|---|--:|--:|--:|
| Storage.IndexFileTests | 7 | 1032.1 | 13 % |
| W3C.SparqlConformanceTests | 997 | 785.0 | 10 % |
| Sparql.LoadExecutorStressTests | 15 | 758.2 | 9 % |
| Storage.PruningTests | 16 | 606.3 | 7 % |
| Infrastructure.AllocationTests | 7 | 484.2 | 6 % |
| Storage.CrashRecoveryTests | 10 | 480.0 | 6 % |
| Storage.AtomStoreTests | 46 | 409.6 | 5 % |
| Storage.QuadIndexCursorTests | 7 | 391.4 | 5 % |
| Infrastructure.ConcurrentAccessTests | 15 | 388.0 | 5 % |
| Storage.TemporalQuadIndexTests | 32 | 306.4 | 4 % |
| Rdf.StreamingIoTests | 15 | 281.0 | 3 % |
| Storage.ReferenceQuadIndexTests | 16 | 259.0 | 3 % |
| Sparql.NowTests | 14 | 169.2 | 2 % |
| Sparql.QueryExecutorTests | 231 | 98.5 | 1 % |
| Sparql.SparqlTextSearchTests | 14 | 93.4 | 1 % |
| Sparql.PropertyPathRegressionTests | 3 | 93.1 | 1 % |
| Storage.QuadStoreTests | 51 | 78.1 | 1 % |
| Storage.DeleteEndsValidTimeTests | 16 | 76.5 | 1 % |
| Sparql.GraphTreeEvaluatorTests | 33 | 73.3 | 1 % |
| Storage.BulkLoadTests | 7 | 71.8 | 1 % |
| Sparql.SparqlHttpServerTests | 34 | 54.2 | 1 % |
| Diagnostics.AtomStoreMetricsTests | 6 | 43.1 | 1 % |
| Sparql.TreeJoinExecutorTests | 27 | 40.5 | 0 % |
| Storage.MinimalQuadIndexTests | 10 | 39.5 | 0 % |
| Diagnostics.MetricEmissionThrottleTests | 7 | 39.1 | 0 % |
| Infrastructure.BufferManagerTests | 20 | 38.0 | 0 % |
| Sparql.LongExpansionTests | 7 | 37.2 | 0 % |
| Storage.MemoryTimeWriteRuleTests | 38 | 36.5 | 0 % |
| Sparql.MemoryWordsTests | 48 | 35.4 | 0 % |
| Storage.MemoryTimeRecoveryTests | 10 | 34.0 | 0 % |

| Test | Class | Duration, s |
|---|---|--:|
| Stress ZeroLimits ShouldLoadEverything | Sparql.LoadExecutorStressTests | 546.3 |
| LargeTransfer BatchesCorrectly | Storage.PruningTests | 527.3 |
| ReadersOfAStoreAtTheSameTime FindEveryQuad | Storage.IndexFileTests | 493.9 |
| ManySessions EachKilled KeepEveryBatch | Storage.CrashRecoveryTests | 459.9 |
| GraphPath Limit PushesIntoScan | Infrastructure.AllocationTests | 331.8 |
| TimeFirst TemporalRangeQuery VisitsFewerPages | Storage.TemporalQuadIndexTests | 278.7 |
| AStoreThatOutgrowsItsIndexFiles HoldsEveryQuad AlsoWhenOpenedAgain(profile: Cognitive) | Storage.IndexFileTests | 230.3 |
| Intern HighCollisionLoad HandlesGracefully | Storage.AtomStoreTests | 221.6 |
| AStoreThatOutgrowsItsIndexFiles HoldsEveryQuad AlsoWhenOpenedAgain(profile: Graph) | Storage.IndexFileTests | 192.6 |
| StressTest HighConcurrency NoExceptions | Infrastructure.ConcurrentAccessTests | 146.5 |
| GraphPath HotScanLoop IsZeroGcPerScanStep | Infrastructure.AllocationTests | 140.7 |
| LoadFileAsync ChunkedCommits AllTriplesQueryable | Rdf.StreamingIoTests | 137.9 |
| Stress ConcurrentLoads ShouldNotCorrupt | Sparql.LoadExecutorStressTests | 113.5 |
| AppendSorted LargeMonotonicRun ExceedsLeafAndPromotes | Storage.ReferenceQuadIndexTests | 111.1 |
| GetAtomId AfterManyInserts StillFindsEntries | Storage.AtomStoreTests | 104.0 |
| ConcurrentWrites MultipleThreads AllSucceed | Infrastructure.ConcurrentAccessTests | 102.2 |
| Aggregate OverStoredTriples ReadsOneValue | Sparql.NowTests | 97.7 |
| ReadersAtTheSameTime AreEachGivenThePageTheyAskedFor | Storage.IndexFileTests | 92.8 |
| Add ManyInSortedOrder PreservesUniqueness | Storage.ReferenceQuadIndexTests | 90.3 |
| TransitivePath HonorsCancellationToken | Sparql.PropertyPathRegressionTests | 88.7 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: EntityFirst) | Storage.QuadIndexCursorTests | 86.8 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: TimeFirst) | Storage.QuadIndexCursorTests | 86.7 |
| Stress RapidSuccessiveLoads ShouldNotLeakResources | Sparql.LoadExecutorStressTests | 80.8 |
| LoadFileAsync ProgressReporting FiresAtChunkBoundaries | Rdf.StreamingIoTests | 66.4 |
| TheReferenceIndexsCursor GivesEveryEntryItsQuerySelects InKeyOrder | Storage.QuadIndexCursorTests | 66.1 |
| TheGraphIndexsCursor GivesEveryEntryItsQuerySelects WithItsVersion InKeyOrder | Storage.QuadIndexCursorTests | 65.1 |
| DeleteCurrent PeriodsSpanningLeafPages FindsTheOneThatHolds | Storage.DeleteEndsValidTimeTests | 56.3 |
| TheMinimalIndexsCursor GivesEveryEntryItsQuerySelects InKeyOrder | Storage.QuadIndexCursorTests | 54.4 |
| TextMatch TrigramPreFiltering ReducesEvaluations | Sparql.SparqlTextSearchTests | 49.3 |
| Add ExceedsLeafDegree SplitsAndPreservesOrder | Storage.ReferenceQuadIndexTests | 46.7 |

