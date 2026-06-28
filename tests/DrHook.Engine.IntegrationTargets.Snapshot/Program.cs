// DrHook.Engine — Layer 3 LAUNCHABLE INTEGRATION TARGET for the ADR-012 Phase 1 CaptureState snapshot
// test. A plain net10 console app the substrate LAUNCHES under the debugger (DebugSession.Launch +
// entry-module hold-gate — no Debugger.Break crutch, unlike the attach-shaped MTP/VSTest targets).
// Worker.Compute(n, label) gives a two-frame call stack with named arguments and named locals at the
// marked code line, so the integration test can CaptureState a rich, self-contained snapshot. The marker
// token is kept OFF this header comment so the test's text search matches only the code line below
// (lesson from probes 17/18). Bounded loop — the test disposes the Owned session long before it ends.

using System;
using System.Collections.Generic;
using System.Threading;

var worker = new Worker();
const string alphabet = "abcdefghij";
for (int beat = 1; beat <= 600; beat++)
{
    worker.Compute(beat, "tick");
    worker.Scan(alphabet.AsSpan(0, (beat % 7) + 1));  // span length cycles 2..7 — gates `value.Length > 5`
    Thread.Sleep(50);
}

sealed class Worker
{
    public long Total;
    public int Scanned;

    public long Compute(int n, string label)
    {
        int doubled = n * 2;
        long contribution = doubled + label.Length;
        var tags = new List<string> { label };  // a generic local — exercises runtime generic type-name rendering
        Total += contribution;   // SNAPSHOT_HERE
        GC.KeepAlive(tags);
        return Total;
    }

    // `value` is a ReadOnlySpan<char> argument — a ref struct. The ADR-013 D3 test arms a conditional
    // breakpoint `value.Length > 5` here: the substrate reads value.Length DIRECTLY from the span's
    // `_length` field (a ref struct cannot be func-eval'd), so the condition evaluates instead of faulting.
    public void Scan(ReadOnlySpan<char> value)
    {
        Scanned += value.Length;   // SPAN_HERE
    }
}

// Eval-surface probe fixture (ADR-012 Q8 (a)): a static factory returning an object + a parameterless instance
// method, so the FOUNDATION func-eval mechanic — call a static method that returns an object, then call an
// INSTANCE method on that result (chaining the raw ICorDebugValue as `this`) — can be validated WITHOUT any
// cooperation in the call itself. This is the building block for driving a target's own framework render APIs.
sealed class EvalProbe
{
    private readonly int _seed;
    public EvalProbe(int seed) => _seed = seed;   // public ctor — NewObject (mechanic 4) constructs new EvalProbe(n)
    public static EvalProbe Create() => new EvalProbe(41);
    public int NextValue() => _seed + 1;   // 42 — the chained static→instance call proof

    // Getter-chain fixture, shaped like the Avalonia capture chain (static getter root → instance getters →
    // terminal value): Origin.Inner.Tag mirrors Application.Current → .ApplicationLifetime → .MainWindow.
    public static EvalProbe Origin => Create();         // static getter → object (seed 41)  ~ Application.Current
    public EvalProbe Inner => new EvalProbe(_seed + 1); // instance getter → object (seed 42) ~ .ApplicationLifetime
    public int Tag => _seed;                            // instance getter → int  (terminal, for the assertion)

    public int AddTo(int x) => _seed + x;               // instance method, this + ONE arg — the Render(window)/Save(path) arity
    public static int LengthOf(string s) => s.Length;   // static + STRING arg — confirms a NewString value (Save's path arg)
    public static int SumPair(Pair p) => p.A + p.B;     // static + VALUE-TYPE arg — the new RenderTargetBitmap(PixelSize) shape
}

// A two-int struct shaped like PixelSize(int width, int height) — mechanic 6: NewObject allocates + constructs it
// via this ctor (value-type construction in one step), then it's passed by value to a call. (CreateValueForType +
// setting its contents was a dead end — the synthetic value exposes only ICorDebugValue2.)
struct Pair
{
    public int A;
    public int B;
    public Pair(int a, int b) { A = a; B = b; }
}
