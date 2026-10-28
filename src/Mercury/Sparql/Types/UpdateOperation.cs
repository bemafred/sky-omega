namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Represents a parsed SPARQL Update operation.
/// </summary>
internal struct UpdateOperation
{
    public QueryType Type;
    public Prologue Prologue;

    // INSERT DATA / DELETE DATA: [49] QuadData, a template without variables, read by the template grammar and
    // instantiated as a template is
    public TemplateQuad[] InsertData;
    public TemplateQuad[] DeleteData;

    // For DELETE/INSERT ... WHERE - template patterns ([52] QuadPattern). DELETE WHERE's QuadPattern is its
    // DeleteTemplate as well as its WHERE.
    public TemplateQuad[] DeleteTemplate;
    public TemplateQuad[] InsertTemplate;

    // ADR-052: source offset of the WHERE group's opening '{' (for DELETE WHERE, its QuadPattern's '{').
    // The WHERE is a GroupGraphPattern, parsed into the pattern tree from here and evaluated by TreeJoinExecutor,
    // exactly as a query's WHERE is.
    public int WhereGroupStart;

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
