### Windows x64: the temporary directory on D

- Windows x64: the temporary directory on D
- RealTimeProtectionEnabled=False
- GetTempPath=D:\mops-temp\
- Mercury.Tests: 415 s, exit code 2

6730 tests, 400 not passed. First start to last end: 412 s. The tests' durations add up to 1540 s.

| Test class | Tests | Their durations, s | Share |
|---|--:|--:|--:|
| Storage.IndexFileTests | 7 | 192.5 | 13 % |
| W3C.SparqlConformanceTests | 997 | 144.3 | 9 % |
| Infrastructure.ConcurrentAccessTests | 15 | 126.5 | 8 % |
| Sparql.LoadExecutorStressTests | 15 | 113.7 | 7 % |
| Storage.PruningTests | 16 | 101.3 | 7 % |
| Storage.QuadIndexCursorTests | 7 | 57.4 | 4 % |
| Rdf.StreamingIoTests | 15 | 52.1 | 3 % |
| Storage.AtomStoreTests | 46 | 45.7 | 3 % |
| Infrastructure.AllocationTests | 7 | 45.6 | 3 % |
| Diagnostics.MetricEmissionThrottleTests | 7 | 42.7 | 3 % |
| Compression.OrderedParallelPipelinePoCTests | 9 | 41.3 | 3 % |
| Storage.QuadStoreTests | 51 | 35.2 | 2 % |
| Diagnostics.MetricsListenerTests | 18 | 31.3 | 2 % |
| Sparql.SparqlHttpServerTests | 34 | 27.5 | 2 % |
| Sparql.GraphTreeEvaluatorTests | 33 | 23.6 | 2 % |
| Sparql.MemoryWordsTests | 48 | 22.8 | 1 % |
| Storage.CrashRecoveryTests | 10 | 22.0 | 1 % |
| Storage.TemporalQuadIndexTests | 32 | 22.0 | 1 % |
| Storage.WriteAheadLogTests | 34 | 20.8 | 1 % |
| Storage.SortedAtomStoreExternalBuilderTests | 12 | 20.1 | 1 % |
| Sparql.PropertyPathRegressionTests | 3 | 18.7 | 1 % |
| Diagnostics.AtomStoreMetricsTests | 6 | 18.2 | 1 % |
| Repl.ReplSessionTests | 59 | 16.5 | 1 % |
| Storage.MemoryTimeRecoveryTests | 10 | 14.5 | 1 % |
| Sparql.TreeJoinExecutorTests | 27 | 14.1 | 1 % |
| Storage.BulkLoadTests | 7 | 13.9 | 1 % |
| Repl.RunInteractiveTests | 31 | 12.7 | 1 % |
| Storage.FactStatesTests | 11 | 11.0 | 1 % |
| Storage.SessionMutationFlagTests | 12 | 10.3 | 1 % |
| Sparql.PropertyPathW3CConformanceTests | 21 | 9.8 | 1 % |

| Test | Class | Duration, s |
|---|---|--:|
| ReadersOfAStoreAtTheSameTime FindEveryQuad | Storage.IndexFileTests | 88.9 |
| LargeTransfer BatchesCorrectly | Storage.PruningTests | 79.1 |
| Stress ZeroLimits ShouldLoadEverything | Sparql.LoadExecutorStressTests | 75.8 |
| StressTest HighConcurrency NoExceptions | Infrastructure.ConcurrentAccessTests | 56.6 |
| AStoreThatOutgrowsItsIndexFiles HoldsEveryQuad AlsoWhenOpenedAgain(profile: Cognitive) | Storage.IndexFileTests | 52.8 |
| AStoreThatOutgrowsItsIndexFiles HoldsEveryQuad AlsoWhenOpenedAgain(profile: Graph) | Storage.IndexFileTests | 42.8 |
| ConcurrentCallers ExactlyOneReturnsTruePerInterval | Diagnostics.MetricEmissionThrottleTests | 42.6 |
| GraphPath Limit PushesIntoScan | Infrastructure.AllocationTests | 30.8 |
| Intern HighCollisionLoad HandlesGracefully | Storage.AtomStoreTests | 29.5 |
| JsonlMetricsListener ConcurrentWrites NoTornRecords | Diagnostics.MetricsListenerTests | 28.1 |
| ConcurrentWrites MultipleThreads AllSucceed | Infrastructure.ConcurrentAccessTests | 25.1 |
| ManyWorkersFewItems StillCorrect | Compression.OrderedParallelPipelinePoCTests | 23.6 |
| LoadFileAsync ChunkedCommits AllTriplesQueryable | Rdf.StreamingIoTests | 22.7 |
| Stress ConcurrentLoads ShouldNotCorrupt | Sparql.LoadExecutorStressTests | 21.0 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: EntityFirst) | Storage.QuadIndexCursorTests | 18.7 |
| External ManyChunks ExceedsPoolCapacity StillCorrect | Storage.SortedAtomStoreExternalBuilderTests | 18.5 |
| TransitivePath HonorsCancellationToken | Sparql.PropertyPathRegressionTests | 17.9 |
| MixedReadWrite ConcurrentOperations NoDeadlock | Infrastructure.ConcurrentAccessTests | 17.7 |
| WorkerThrowing PropagatesAndDoesNotHang | Compression.OrderedParallelPipelinePoCTests | 17.5 |
| ManyRecords AllRecoverable | Storage.WriteAheadLogTests | 14.6 |
| LoadFileAsync ProgressReporting FiresAtChunkBoundaries | Rdf.StreamingIoTests | 14.5 |
| TimeFirst TemporalRangeQuery VisitsFewerPages | Storage.TemporalQuadIndexTests | 13.8 |
| GraphPath HotScanLoop IsZeroGcPerScanStep | Infrastructure.AllocationTests | 12.4 |
| ManySessions EachKilled KeepEveryBatch | Storage.CrashRecoveryTests | 11.4 |
| Stress RapidSuccessiveLoads ShouldNotLeakResources | Sparql.LoadExecutorStressTests | 10.9 |
| QuadStore ConcurrentWrites AtomInterningConsistent | Infrastructure.ConcurrentAccessTests | 10.7 |
| TheGraphIndexsCursor GivesEveryEntryItsQuerySelects WithItsVersion InKeyOrder | Storage.QuadIndexCursorTests | 10.1 |
| BulkLoad BatchBufferClearedBetweenChunks | Storage.BulkLoadTests | 8.3 |
| GetAtomId AfterManyInserts StillFindsEntries | Storage.AtomStoreTests | 8.3 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: TimeFirst) | Storage.QuadIndexCursorTests | 8.1 |

