#!/usr/bin/env -S dotnet
#:project ../../src/DrHook.Engine/DrHook.Engine.csproj
//
// DrHook.Engine probe — BRAID VISUALIZATION LEGIBILITY (ADR-012 Phase 3, D4 — Emergence)
// =====================================================================================
//
// Question this probe settles: does a (hypothesis, observation) BRAID — the operator's stated
// prediction emitted next to the REAL runtime state it predicted — read as a legible learning
// trajectory when rendered inline? And does an immutable, append-only WRONG→CORRECTED pair read
// as a trajectory rather than as noise?
//
// It modifies NO substrate. It drives a REAL DebugSession (so every observation is genuine runtime
// state — locals, a span's _length, this's field), interleaves the verbatim hypotheses the operator
// would state (hard-coded here as the predictions), and renders the braid in the console view's idiom.
// One inspection hypothesis is DELIBERATELY WRONG (span.Length = 8 for "hello"); the correction is a
// NEW beat appended after — the wrong one is RETAINED (append-only / immutable), which is the
// discriminating test for why immutability matters.
//
// What it PROVES: the braid builds from real observations; per-stop pairing holds; structured (value)
// hypotheses auto-reconcile (✓/✗) while free-text ones stay implicit; the wrong→corrected pair is
// retained, not edited. What it does NOT prove (out of scope): transport delivery (already proven),
// the persisted bitemporal schema (that is the ADR-012 Phase 3 decision this probe informs), or that
// the REAL ConsoleDebugStateView renders byte-identically (this mimics its idiom). The LEGIBILITY
// verdict is deliberately handed to the human reader.
//
// Falsification: 2 missing target/marker/build; 4 Launch threw; 5 no setup stop; 6 bp did not bind;
//   7 did not stop at BRAID_MARK; 8 a real observation could not be read; 9 the wrong→corrected pair
//   was not retained (append-only violated); 0 PASS (braid rendered — read it and judge legibility).
//
// Usage:  dotnet run --no-cache braid-view-smoke.cs        (run from poc/drhook-engine)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using SkyOmega.DrHook.Engine;

return BraidView.Run();

sealed class SilentSink : IDebugEventSink
{
    public void OnEvent(string name) { }
    public void OnLog(LogRecord record) { }
}

// One beat of the braid. The timeline is APPEND-ONLY — a correction is a new beat, never a mutation.
enum Move { Hyp, See, Stop }
enum Lens { Nav, Look, None }
sealed record Beat(Move Move, Lens Lens, string Text, bool? Matches = null);

static class BraidView
{
    const string TargetRel = "braid-probe-target.cs";

    public static int Run()
    {
        string target = Path.GetFullPath(TargetRel);
        if (!File.Exists(target)) { Console.Error.WriteLine($"FALSIFIED (missing target): {target}. Run from poc/drhook-engine."); return 2; }

        int mark = MarkerLine(target, "BRAID_MARK");
        if (mark < 0) { Console.Error.WriteLine("FALSIFIED (marker not found)."); return 2; }

        string? dll = BuildTarget(target);
        if (dll is null || !File.Exists(dll)) { Console.Error.WriteLine($"FALSIFIED (build): could not build {target}."); return 2; }
        Console.WriteLine($"runtime    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"target dll : {dll}");
        Console.WriteLine($"marker     : BRAID_MARK={mark}");

        DebugSession session;
        try { session = DebugSession.Launch("dotnet", new[] { "exec", dll }, Path.GetDirectoryName(dll), new SilentSink()); }
        catch (Exception ex) { Console.Error.WriteLine($"FALSIFIED (Launch): {ex.GetType().Name}: {ex.Message}"); return 4; }
        Console.WriteLine($"launched   : pid {session.ProcessId}");

        int code = Drive(session, target, mark);
        try { session.Dispose(); } catch { /* Owned target torn down */ }
        return code;
    }

    static int Drive(DebugSession session, string source, int mark)
    {
        StopInfo? setup = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (setup is null || setup.Reason != StopReason.Break) { Console.Error.WriteLine($"FALSIFIED (no setup stop): {(setup?.Reason.ToString() ?? "timeout")}."); return 5; }

        var braid = new List<Beat>();

        // ── Navigation hypothesis: predict WHERE execution goes; pairs with the resulting stop. ──
        braid.Add(new Beat(Move.Hyp, Lens.Nav,
            "continuing — expect to stop at BRAID_MARK in Widget.Describe, with doubled = _seed*2 already computed"));

        int bp = session.SetBreakpointAtLine(source, mark);
        if (bp == 0) { Console.Error.WriteLine("FALSIFIED (binding): breakpoint did not bind."); return 6; }
        session.Resume();
        StopInfo? stop = session.WaitForStop(TimeSpan.FromSeconds(15));
        if (stop is null || stop.Reason != StopReason.Breakpoint) { Console.Error.WriteLine($"FALSIFIED: expected Breakpoint stop; got {(stop?.Reason.ToString() ?? "timeout")}."); return 7; }

        string frame = session.GetStackFrames().FirstOrDefault() ?? "(no frame)";
        braid.Add(new Beat(Move.Stop, Lens.None, frame));

        // The observation snapshot a viewer would already show (real state) — the nav hypothesis's pairing.
        long? doubled = LocalInt(session, "doubled");
        long? total   = LocalInt(session, "total");
        string label  = ArgStr(session, "label");
        string spanTy = session.GetLocals().FirstOrDefault(l => l.Name == "span").TypeName ?? "?";
        braid.Add(new Beat(Move.See, Lens.Nav,
            $"doubled={Fmt(doubled)}  span={{{Short(spanTy)}}}  total={Fmt(total)}  label=\"{label}\""));
        // nav reconciliation is left IMPLICIT (free-text) — the reader sees we stopped in Describe as predicted.

        // ── Inspection hypotheses: predict CONTENTS; structured value-predictions auto-reconcile. ──
        long? spanLen = SpanLength(session, "span");
        long? seed    = ThisField(session, "_seed");
        if (doubled is null || total is null || spanLen is null || seed is null)
        { Console.Error.WriteLine($"FALSIFIED (read): doubled={Fmt(doubled)} total={Fmt(total)} span.Length={Fmt(spanLen)} this._seed={Fmt(seed)}."); return 8; }

        Inspect(braid, "doubled = 14 (7 * 2)",                       predicted: 14, observedText: "doubled",      observed: doubled.Value);
        Inspect(braid, "span.Length = 8",                            predicted: 8,  observedText: "span.Length",  observed: spanLen.Value); // DELIBERATELY WRONG
        Inspect(braid, "span.Length = 5 — miscounted \"hello\" (h-e-l-l-o); correction, prior hypothesis retained",
                                                                     predicted: 5,  observedText: "span.Length",  observed: spanLen.Value); // correction
        Inspect(braid, "total = 19 (doubled 14 + span.Length 5)",    predicted: 19, observedText: "total",        observed: total.Value);
        Inspect(braid, "this._seed = 7",                             predicted: 7,  observedText: "this._seed",   observed: seed.Value);

        Render(braid);

        // Append-only / immutability check: BOTH the wrong span hypothesis and its correction are retained.
        int spanHyps = braid.Count(b => b.Move == Move.Hyp && b.Text.StartsWith("span.Length", StringComparison.Ordinal));
        if (spanHyps < 2) { Console.Error.WriteLine($"FALSIFIED (append-only): the wrong→corrected span hypotheses were not both retained (found {spanHyps})."); return 9; }

        Console.WriteLine();
        Console.WriteLine("PROBE RAN — braid built from REAL observations, per-stop paired, structured hypotheses auto-reconciled");
        Console.WriteLine("(✓/✗), free-text nav reconciliation left implicit, and the wrong→corrected span pair RETAINED (append-only).");
        Console.WriteLine(">>> LEGIBILITY IS YOURS TO JUDGE: read the braid above — does hypothesis ▸ observation read as a");
        Console.WriteLine(">>> trajectory? Does the span.Length 8 ✗ → 5 ✓ pair read as a learning correction, or as noise?");
        return 0;
    }

    // Append a structured inspection beat: the hypothesis, then the real observation with an auto-verdict.
    static void Inspect(List<Beat> braid, string hypothesis, long predicted, string observedText, long observed)
    {
        braid.Add(new Beat(Move.Hyp, Lens.Look, hypothesis));
        braid.Add(new Beat(Move.See, Lens.Look, $"{observedText} = {observed.ToString(CultureInfo.InvariantCulture)}", Matches: predicted == observed));
    }

    static void Render(IReadOnlyList<Beat> braid)
    {
        Console.WriteLine();
        Console.WriteLine("── braid (hypothesis ▸ … then the observation it predicted) ────────────────────");
        foreach (Beat b in braid)
        {
            switch (b.Move)
            {
                case Move.Hyp:
                    string tag = b.Lens == Lens.Nav ? "hyp·nav " : "hyp·look";
                    Console.WriteLine($"  {tag} ▸ {b.Text}");
                    break;
                case Move.Stop:
                    Console.WriteLine($"  ► stop     {b.Text}");
                    break;
                case Move.See:
                    string verdict = b.Matches switch { true => "✓ matches", false => "✗ contradicts", null => "(reconciliation in chat)" };
                    Console.WriteLine($"  observe    {b.Text,-44} {verdict}");
                    break;
            }
        }
        Console.WriteLine("────────────────────────────────────────────────────────────────────────────────");
    }

    // ── real-observation reads off the live session ──
    static long? LocalInt(DebugSession s, string name)
        => ToLong(s.GetLocals().FirstOrDefault(l => l.Name == name).RawValue);

    static string ArgStr(DebugSession s, string name)
    {
        ArgumentValue a = s.GetArguments().FirstOrDefault(x => x.Name == name);
        return a.StringValue ?? (a.RawValue?.ToString() ?? "?");
    }

    static long? SpanLength(DebugSession s, string localName)
        => ToLong(s.ExpandLocal(localName, Array.Empty<string>()).FirstOrDefault(f => f.Name == "_length").RawValue);

    static long? ThisField(DebugSession s, string fieldName)
        => ToLong(s.ExpandArgument(0, Array.Empty<string>()).FirstOrDefault(f => f.Name == fieldName).RawValue);

    static long? ToLong(object? raw) => raw is null ? null : Convert.ToInt64(raw, CultureInfo.InvariantCulture);
    static string Fmt(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "(null)";
    static string Short(string typeName)
    {
        int lt = typeName.IndexOf('<');
        string head = lt < 0 ? typeName : typeName[..lt];
        int dot = head.LastIndexOf('.');
        string shortHead = dot < 0 ? head : head[(dot + 1)..];
        return lt < 0 ? shortHead : shortHead + typeName[lt..].Replace("System.", "");
    }

    static string? BuildTarget(string targetCs)
    {
        var psi = new ProcessStartInfo("dotnet", $"build \"{targetCs}\" -c Debug -v:m")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using Process p = Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        foreach (string line in outp.Split('\n'))
        {
            int arrow = line.IndexOf("-> ", StringComparison.Ordinal);
            if (arrow < 0) continue;
            string path = line[(arrow + 3)..].Trim();
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return path;
        }
        Console.Error.WriteLine(outp);
        Console.Error.WriteLine(err);
        return null;
    }

    static int MarkerLine(string path, string marker)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.Ordinal)) return i + 1;
        return -1;
    }
}
