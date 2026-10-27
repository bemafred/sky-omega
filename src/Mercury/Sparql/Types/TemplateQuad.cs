namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// One triple of an Update template ([52] QuadPattern), with the graph of the GRAPH block it is written in. A template
/// is an array of these, as long as the text it was parsed from: [50] Quads puts no bound on its triples or GRAPH
/// blocks.
/// </summary>
internal struct TemplateQuad
{
    public Term Subject;
    public Term Predicate;
    public Term Object;

    /// <summary>The GRAPH block's VarOrIri; zero length for a triple outside any GRAPH block (the WITH graph, or the
    /// default graph).</summary>
    public Term Graph;

    public readonly bool InGraphBlock => Graph.Length > 0;
}
