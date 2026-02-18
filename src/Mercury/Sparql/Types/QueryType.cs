namespace SkyOmega.Mercury.Sparql.Types;

internal enum QueryType
{
    Unknown,
    Select,
    Construct,
    Describe,
    Ask,
    // SPARQL Update operations
    InsertData,
    DeleteData,
    DeleteWhere,
    Modify,  // DELETE/INSERT ... WHERE
    Load,
    Clear,
    Create,
    Drop,
    Copy,
    Move,
    Add
}
