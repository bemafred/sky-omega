// Integration tests spawn target processes + attach via DrHook substrate. Running
// multiple in parallel collides on mscordbi state and process-tree lifecycle.
// Force sequential execution at the assembly level. The tradeoff (slower wall-clock
// for the whole suite) is acceptable — these are substrate-validation tests, not
// fast unit tests.

using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]
