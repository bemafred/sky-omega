### Linux x64: as the runner is set up

- Linux x64: as the runner is set up
- GetTempPath=/tmp/
- Mercury.Tests: 148 s, exit code 0

6730 tests, 0 not passed. First start to last end: 146 s. The tests' durations add up to 505 s.

| Test class | Tests | Their durations, s | Share |
|---|--:|--:|--:|
| Storage.SortedAtomStoreExternalBuilderTests | 12 | 60.2 | 12 % |
| Infrastructure.AllocationTests | 7 | 29.5 | 6 % |
| W3C.SparqlConformanceTests | 997 | 26.8 | 5 % |
| Storage.IndexFileTests | 7 | 24.4 | 5 % |
| Diagnostics.MetricEmissionThrottleTests | 7 | 23.5 | 5 % |
| Storage.QuadIndexCursorTests | 7 | 22.6 | 4 % |
| Storage.QuadStoreTests | 51 | 20.8 | 4 % |
| Storage.PruningTests | 16 | 20.0 | 4 % |
| Infrastructure.ConcurrentAccessTests | 15 | 18.4 | 4 % |
| Storage.TemporalQuadIndexTests | 32 | 18.0 | 4 % |
| Storage.CrashRecoveryTests | 10 | 17.4 | 3 % |
| Sparql.LoadExecutorStressTests | 15 | 12.5 | 2 % |
| Compression.ParallelBZip2DecompressorStreamTests | 20 | 10.1 | 2 % |
| Storage.AtomStoreTests | 46 | 8.2 | 2 % |
| Rdf.StreamingIoTests | 15 | 6.7 | 1 % |
| Sparql.SparqlHttpServerAddressesTests | 13 | 6.7 | 1 % |
| Storage.QuadStorePoolTests | 10 | 5.8 | 1 % |
| Serving.AttachAndVersionTests | 7 | 5.8 | 1 % |
| Sparql.SparqlTextSearchTests | 14 | 5.8 | 1 % |
| Storage.SortedAtomBulkBuilderDiskBackedTests | 10 | 5.6 | 1 % |
| Sparql.PropertyPathRegressionTests | 3 | 5.4 | 1 % |
| Sparql.GraphTreeEvaluatorTests | 33 | 5.0 | 1 % |
| Storage.ReferenceQuadIndexTests | 16 | 4.7 | 1 % |
| Storage.QuadStoreProfileDispatchTests | 16 | 4.7 | 1 % |
| Storage.WriteAheadLogTests | 34 | 4.6 | 1 % |
| Storage.MemoryTimeWriteRuleTests | 38 | 4.6 | 1 % |
| Repl.RunInteractiveTests | 31 | 4.5 | 1 % |
| Storage.MemoryTimeRecoveryTests | 10 | 4.4 | 1 % |
| Storage.ReferenceRebuildTests | 7 | 3.9 | 1 % |
| Diagnostics.AtomStoreMetricsTests | 6 | 3.7 | 1 % |

| Test | Class | Duration, s |
|---|---|--:|
| External ManyChunks ExceedsPoolCapacity StillCorrect | Storage.SortedAtomStoreExternalBuilderTests | 55.6 |
| ConcurrentCallers ExactlyOneReturnsTruePerInterval | Diagnostics.MetricEmissionThrottleTests | 23.4 |
| LargeTransfer BatchesCorrectly | Storage.PruningTests | 15.8 |
| TimeFirst TemporalRangeQuery VisitsFewerPages | Storage.TemporalQuadIndexTests | 15.8 |
| GraphPath Limit PushesIntoScan | Infrastructure.AllocationTests | 15.4 |
| ReadersOfAStoreAtTheSameTime FindEveryQuad | Storage.IndexFileTests | 14.8 |
| GraphPath HotScanLoop IsZeroGcPerScanStep | Infrastructure.AllocationTests | 11.2 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: EntityFirst) | Storage.QuadIndexCursorTests | 10.7 |
| Stress ZeroLimits ShouldLoadEverything | Sparql.LoadExecutorStressTests | 10.6 |
| StressTest HighConcurrency NoExceptions | Infrastructure.ConcurrentAccessTests | 7.2 |
| Clear MultipleTimes Succeeds | Storage.QuadStoreTests | 7.1 |
| ManySessions EachKilled KeepEveryBatch | Storage.CrashRecoveryTests | 7.0 |
| Attach NothingThere SaysSo AndExitsWith1 | Serving.AttachAndVersionTests | 5.8 |
| AStoreThatOutgrowsItsIndexFiles HoldsEveryQuad AlsoWhenOpenedAgain(profile: Cognitive) | Storage.IndexFileTests | 5.1 |
| TransitivePath HonorsCancellationToken | Sparql.PropertyPathRegressionTests | 4.8 |
| TheReferenceIndexsCursor GivesEveryEntryItsQuerySelects InKeyOrder | Storage.QuadIndexCursorTests | 4.1 |
| ConcurrentWrites MultipleThreads AllSucceed | Infrastructure.ConcurrentAccessTests | 3.1 |
| DiskBacked MultiChunkOccurrenceSpill EquivalentToInMemory | Storage.SortedAtomBulkBuilderDiskBackedTests | 3.1 |
| AStoreThatOutgrowsItsIndexFiles HoldsEveryQuad AlsoWhenOpenedAgain(profile: Graph) | Storage.IndexFileTests | 3.1 |
| KilledInsideDispose KeepsTheCommittedBatch(delayMs: 30) | Storage.CrashRecoveryTests | 3.0 |
| Clear ThenReopen BringsNoClearedAtomBack | Storage.AtomStoreRecoveryTests | 3.0 |
| TheCognitiveIndexsCursor GivesEveryPeriodItsQuerySelects InTheIndexsOrder(order: TimeFirst) | Storage.QuadIndexCursorTests | 2.9 |
| OneServer AnswersOnEveryAddress | Sparql.SparqlHttpServerAddressesTests | 2.8 |
| Clear SetsFlag | Storage.SessionMutationFlagTests | 2.7 |
| Clear NamedGraphs AlsoCleared | Storage.QuadStoreTests | 2.6 |
| ManyRecords AllRecoverable | Storage.WriteAheadLogTests | 2.6 |
| MultiBlock RandomChunkReads PreserveOrdering | Compression.ParallelBZip2DecompressorStreamTests | 2.5 |
| Clear StoreWithData RemovesAllQuads | Storage.QuadStoreTests | 2.5 |
| QuadStore QueryIteration ZeroAllocations | Infrastructure.AllocationTests | 2.4 |
| Clear StatisticsReset | Storage.QuadStoreTests | 2.4 |

