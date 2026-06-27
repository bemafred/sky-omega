namespace SkyOmega.DrHook.Viz;

/// <summary>Why a <see cref="SourceWindow"/> is or is not renderable. <see cref="Ok"/> is the only status that
/// carries lines; every other value is a named, falsifiable reason a view shows in place of source (never an
/// exception — reading arbitrary files from disk is best-effort, ADR-012 Phase 4). The distinctions matter to a
/// human: <see cref="FileNotFound"/> means the source is not co-located / moved (the structured path came over
/// the wire but points nowhere on this machine), whereas <see cref="LineOutOfRange"/> means the file IS here but
/// the PDB line falls outside it — the classic source-vs-binary drift the checksum guard (a later hardening,
/// embedded-source PDBs) will catch.</summary>
public enum SourceWindowStatus
{
    /// <summary>A window of lines around the current line was produced.</summary>
    Ok,

    /// <summary>The frame carried no resolved location — running, a native/internal frame, or no PDB. The
    /// position has nothing to point at, so there is no source to show (not an error).</summary>
    NoLocation,

    /// <summary>The resolved path does not exist on this machine — the source tree is not co-located, or the
    /// file moved since the binary was built.</summary>
    FileNotFound,

    /// <summary>The file exceeds the per-file read bound (<see cref="SourceWindowOptions.MaxFileBytes"/>) —
    /// refused rather than read, so one pathological path cannot blow the view's memory.</summary>
    FileTooLarge,

    /// <summary>The file exists but could not be read (I/O or access error).</summary>
    ReadError,

    /// <summary>The file was read but the current line is outside its 1..N range — source/binary drift.</summary>
    LineOutOfRange,
}

/// <summary>One source line in a <see cref="SourceWindow"/>: its 1-based <see cref="Number"/>, the raw
/// <see cref="Text"/> (no trailing newline; tabs preserved — the view decides expansion), and whether it is
/// the line execution is stopped on (<see cref="IsCurrent"/>, which a view marks, e.g. a gutter caret).</summary>
public readonly record struct SourceLine(int Number, string Text, bool IsCurrent);

/// <summary>A view-agnostic window of source lines around an execution position — the artifact a Phase-4 view
/// renders to show *the code* execution is stopped in, not just "Type.Method @ file:line". Produced by
/// <see cref="SourceWindowReader"/> from the structured location the Phase-2 enrichment put on the wire
/// (<c>WireFrame.File</c>/<c>Line</c>). It is a MODEL, not styled text: a console view joins the lines with a
/// caret on <see cref="SourceLine.IsCurrent"/>; a TUI/GUI styles them (gutter, highlight) — so this primitive
/// is independent of the still-open TUI-technology question (ADR-012 Q2). <see cref="Lines"/> is empty unless
/// <see cref="Status"/> is <see cref="SourceWindowStatus.Ok"/>.</summary>
public sealed record SourceWindow(
    SourceWindowStatus Status,
    string? FilePath,
    int CurrentLine,
    IReadOnlyList<SourceLine> Lines)
{
    /// <summary>True when a renderable window of source is present.</summary>
    public bool HasSource => Status == SourceWindowStatus.Ok && Lines.Count > 0;

    /// <summary>The empty window for a position with no source to show (running / no PDB / no location).</summary>
    public static SourceWindow None { get; } =
        new(SourceWindowStatus.NoLocation, null, 0, Array.Empty<SourceLine>());
}
