namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Specifies a target graph for graph management operations.
/// </summary>
internal struct GraphTarget
{
    public GraphTargetType Type;
    public int IriStart;
    public int IriLength;
}
