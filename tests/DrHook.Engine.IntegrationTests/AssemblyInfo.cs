// Integration tests spawn target processes + attach via DrHook substrate. Running
// multiple in parallel collides on mscordbi state and process-tree lifecycle.
// Force sequential execution at the assembly level. The tradeoff (slower wall-clock
// for the whole suite) is acceptable — these are substrate-validation tests, not
// fast unit tests.

// Every test also has a 120 s limit (testconfig.json, "mstest.timeout.test"). The engine's native calls —
// attach, detach, dispose, launch — have no timeout of their own, so without it a block in one of them hangs
// the whole run silently (seen on zbook: the Linux launch path spun forever). The slowest test takes ~6 s and
// a test's own internal waits add up to ~90 s at most, so 120 s only ever fires on a hang.

using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]
