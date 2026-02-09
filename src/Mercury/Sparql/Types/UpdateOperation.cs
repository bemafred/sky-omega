namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Represents a parsed SPARQL Update operation.
/// </summary>
public struct UpdateOperation
{
    public QueryType Type;
    public Prologue Prologue;

    // For INSERT DATA / DELETE DATA - inline triple data
    public QuadData[] InsertData;
    public QuadData[] DeleteData;

    // For DELETE/INSERT ... WHERE - template patterns
    public GraphPattern DeleteTemplate;
    public GraphPattern InsertTemplate;
    public WhereClause WhereClause;

    // For USING clause in DELETE/INSERT
    public DatasetClause[] UsingClauses;

    // For WITH clause in DELETE/INSERT WHERE
    public int WithGraphStart;
    public int WithGraphLength;

    // For graph management (LOAD, CLEAR, DROP, CREATE, COPY, MOVE, ADD)
    public GraphTarget SourceGraph;
    public GraphTarget DestinationGraph;
    public bool Silent;  // SILENT modifier

    // For LOAD
    public int SourceUriStart;
    public int SourceUriLength;
}
