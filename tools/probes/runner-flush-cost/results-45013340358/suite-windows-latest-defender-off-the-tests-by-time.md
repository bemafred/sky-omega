### Windows x64: Defender's real-time protection off

- Windows x64: Defender's real-time protection off
- RealTimeProtectionEnabled=False
- GetTempPath=C:\Users\runneradmin\AppData\Local\Temp\
- Mercury.Tests: 1749 s, exit code 2

6730 tests, 400 not passed. First start to last end: 1746 s. The tests' durations add up to 6301 s.

| Test class | Tests | Their durations, s | Share |
|---|--:|--:|--:|
| Storage.IndexFileTests | 7 | 837.6 | 13 % |
| W3C.SparqlConformanceTests | 997 | 605.3 | 10 % |
| Sparql.LoadExecutorStressTests | 15 | 597.9 | 9 % |
| Storage.PruningTests | 16 | 481.7 | 8 % |
| Storage.CrashRecoveryTests | 10 | 359.7 | 6 % |
| Infrastructure.AllocationTests | 7 | 348.1 | 6 % |
| Infrastructure.ConcurrentAccessTests | 15 | 310.1 | 5 % |
| Storage.AtomStoreTests | 46 | 304.9 | 5 % |
| Storage.QuadIndexCursorTests | 7 | 284.1 | 5 % |
| Storage.TemporalQuadIndexTests | 32 | 239.7 | 4 % |
| Rdf.StreamingIoTests | 15 | 203.4 | 3 % |
| Storage.ReferenceQuadIndexTests | 16 | 186.0 | 3 % |
| Sparql.NowTests | 14 | 117.1 | 2 % |
| Sparql.QueryExecutorTests | 231 | 74.2 | 1 % |
| Sparql.SparqlTextSearchTests | 14 | 71.0 | 1 % |
| Storage.QuadStoreTests | 51 | 64.2 | 1 % |
| Storage.BulkLoadTests | 7 | 57.8 | 1 % |
| Sparql.PropertyPathRegressionTests | 3 | 57.2 | 1 % |
| Sparql.GraphTreeEvaluatorTests | 33 | 51.6 | 1 % |
| Sparql.SparqlHttpServerTests | 34 | 51.2 | 1 % |
| Diagnostics.MetricEmissionThrottleTests | 7 | 47.8 | 1 % |
| Storage.DeleteEndsValidTimeTests | 16 | 42.4 | 1 % |
| Compression.OrderedParallelPipelinePoCTests | 9 | 41.5 | 1 % |
| Storage.MemoryTimeWriteRuleTests | 38 | 38.6 | 1 % |
| Sparql.LongExpansionTests | 7 | 32.8 | 1 % |
| Storage.MemoryTimeRecoveryTests | 10 | 32.2 | 1 % |
| Diagnostics.AtomStoreMetricsTests | 6 | 32.1 | 1 % |
| Sparql.TreeJoinExecutorTests | 27 | 29.3 | 0 % |
| Infrastructure.BufferManagerTests | 20 | 28.5 | 0 % |
| Storage.WriteAheadLogTests | 34 | 27.7 | 0 % |

| Test | Class | Duration, s |
|---|---|--:|
| Stress ZeroLimits ShouldLoadEverything | Sparql.LoadExecutorStressTests | 423.8 |
| LargeTransfer BatchesCorrectly | Storage.PruningTests | 413.6 |
| ReadersOfAStoreAtTheSameTime FindEveryQuad | Storage.IndexFileTests | 397.5 |
| ManySessions EachKilled KeepEveryBatch | Storage.CrashRecoveryTests | 343.0 |
| GraphPath Limit PushesIntoScan | Infrastructure.AllocationTests | 238.7 |
| AStoreThatOutgrowsItsIndexFiles HoldsEveryQuad AlsoWhenOpenedAgain(profile: Cognitive) | Storage.IndexFileTests | 228.5 |
| TimeFirst TemporalRangeQuery VisitsFewerPages | Storage.TemporalQuadIndexTests | 219.9 |
| Intern HighCollisionLoad HandlesGracefully | Storage.AtomStoreTests | 159.1 |
| AStoreThatOutgrowsItsIndexFiles HoldsEveryQuad AlsoWhenOpenedAgain(profile: Graph) | Storage.IndexFileTests | 118.0 |
| StressTest HighConcurrency NoExceptions | Infrastructure.ConcurrentAccessTests | 102.5 |
| GraphPath HotScanLoop IsZeroGcPerScanStep | Infrastructure.AllocationTests | 101.0 |
| GetAtomId AfterManyInserts StillFindsEntries | Storage.AtomStoreTests | 98.6 |
| LoadFileAsync ChunkedCommits AllTriplesQueryable | Rdf.StreamingIoTests | 98.3 |
| Stress ConcurrentLoads ShouldNotCorrupt | Sparql.LoadExecutorStressTests | 91.2 |
| ConcurrentWrites MultipleThreads AllSucceed | Infrastructure.ConcurrentAccessTests | 85.1 |
| ReadersAtTheSameTime AreEachGivenThePageTheyAskedFor | Storage.IndexFileTests | 78.0 |
| Add ManyInSortedOrder PreservesUniqueness | Storage.ReferenceQuadIndexTests | 77.6 |
| AppendSorted LargeMonotonicRun ExceedsLeafAndPromotes | Storage.ReferenceQuadIndexTests | 74.4 |
| Stress RapidSuccessiveLoads ShouldNotLeakResources | Sparql.LoadExecutorStressTests | 67.8 |
| Aggregate OverStoredTriples ReadsOneValue | Sparql.NowTests | 67.5 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: TimeFirst) | Storage.QuadIndexCursorTests | 63.9 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: EntityFirst) | Storage.QuadIndexCursorTests | 62.1 |
| TransitivePath HonorsCancellationToken | Sparql.PropertyPathRegressionTests | 54.4 |
| TheReferenceIndexsCursor GivesEveryEntryItsQuerySelects InKeyOrder | Storage.QuadIndexCursorTests | 52.0 |
| TheGraphIndexsCursor GivesEveryEntryItsQuerySelects WithItsVersion InKeyOrder | Storage.QuadIndexCursorTests | 47.9 |
| ConcurrentCallers ExactlyOneReturnsTruePerInterval | Diagnostics.MetricEmissionThrottleTests | 47.7 |
| LoadFileAsync ProgressReporting FiresAtChunkBoundaries | Rdf.StreamingIoTests | 46.8 |
| WorkerThrowing PropagatesAndDoesNotHang | Compression.OrderedParallelPipelinePoCTests | 41.4 |
| TheMinimalIndexsCursor GivesEveryEntryItsQuerySelects InKeyOrder | Storage.QuadIndexCursorTests | 38.7 |
| ConcurrentBatchWrites MultipleThreads Serialized | Infrastructure.ConcurrentAccessTests | 36.9 |

