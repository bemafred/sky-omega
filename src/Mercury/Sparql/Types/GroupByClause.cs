namespace SkyOmega.Mercury.Sparql.Types;

internal struct GroupByClause
{
    public const int MaxVariables = 8;
    private int _count;

    // Inline storage for up to 8 grouping variables/aliases (start, length pairs)
    private int _v0Start, _v0Len, _v1Start, _v1Len, _v2Start, _v2Len, _v3Start, _v3Len;
    private int _v4Start, _v4Len, _v5Start, _v5Len, _v6Start, _v6Len, _v7Start, _v7Len;

    // Inline storage for up to 8 grouping expressions (start, length pairs)
    // When expression length is 0, the entry is a simple variable; otherwise it's an expression with alias
    private int _e0Start, _e0Len, _e1Start, _e1Len, _e2Start, _e2Len, _e3Start, _e3Len;
    private int _e4Start, _e4Len, _e5Start, _e5Len, _e6Start, _e6Len, _e7Start, _e7Len;

    public readonly int Count => _count;
    public readonly bool HasGroupBy => _count > 0;

    /// <summary>
    /// Add a simple variable to GROUP BY (e.g., GROUP BY ?x).
    /// </summary>
    public void AddVariable(int start, int length)
    {
        AddExpression(start, length, 0, 0);
    }

    /// <summary>
    /// Add an expression with alias to GROUP BY (e.g., GROUP BY ((?O1 + ?O2) AS ?O12)).
    /// </summary>
    /// <param name="aliasStart">Start of alias variable (e.g., ?O12)</param>
    /// <param name="aliasLength">Length of alias variable</param>
    /// <param name="exprStart">Start of expression (e.g., (?O1 + ?O2))</param>
    /// <param name="exprLength">Length of expression</param>
    public void AddExpression(int aliasStart, int aliasLength, int exprStart, int exprLength)
    {
        if (_count >= MaxVariables)
            throw new SparqlParseException("Too many GROUP BY variables (max 8)");

        switch (_count)
        {
            case 0: _v0Start = aliasStart; _v0Len = aliasLength; _e0Start = exprStart; _e0Len = exprLength; break;
            case 1: _v1Start = aliasStart; _v1Len = aliasLength; _e1Start = exprStart; _e1Len = exprLength; break;
            case 2: _v2Start = aliasStart; _v2Len = aliasLength; _e2Start = exprStart; _e2Len = exprLength; break;
            case 3: _v3Start = aliasStart; _v3Len = aliasLength; _e3Start = exprStart; _e3Len = exprLength; break;
            case 4: _v4Start = aliasStart; _v4Len = aliasLength; _e4Start = exprStart; _e4Len = exprLength; break;
            case 5: _v5Start = aliasStart; _v5Len = aliasLength; _e5Start = exprStart; _e5Len = exprLength; break;
            case 6: _v6Start = aliasStart; _v6Len = aliasLength; _e6Start = exprStart; _e6Len = exprLength; break;
            case 7: _v7Start = aliasStart; _v7Len = aliasLength; _e7Start = exprStart; _e7Len = exprLength; break;
        }
        _count++;
    }

    /// <summary>
    /// Get the variable/alias at the given index.
    /// </summary>
    public readonly (int Start, int Length) GetVariable(int index)
    {
        return index switch
        {
            0 => (_v0Start, _v0Len),
            1 => (_v1Start, _v1Len),
            2 => (_v2Start, _v2Len),
            3 => (_v3Start, _v3Len),
            4 => (_v4Start, _v4Len),
            5 => (_v5Start, _v5Len),
            6 => (_v6Start, _v6Len),
            7 => (_v7Start, _v7Len),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    /// <summary>
    /// Get the expression at the given index. Returns (0, 0) for simple variables.
    /// </summary>
    public readonly (int Start, int Length) GetExpression(int index)
    {
        return index switch
        {
            0 => (_e0Start, _e0Len),
            1 => (_e1Start, _e1Len),
            2 => (_e2Start, _e2Len),
            3 => (_e3Start, _e3Len),
            4 => (_e4Start, _e4Len),
            5 => (_e5Start, _e5Len),
            6 => (_e6Start, _e6Len),
            7 => (_e7Start, _e7Len),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    /// <summary>
    /// Returns true if the entry at the given index is an expression with alias.
    /// </summary>
    public readonly bool IsExpression(int index)
    {
        var (_, len) = GetExpression(index);
        return len > 0;
    }
}
