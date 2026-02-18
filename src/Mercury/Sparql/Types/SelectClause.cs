namespace SkyOmega.Mercury.Sparql.Types;

internal struct SelectClause
{
    public const int MaxAggregates = 8;
    public const int MaxProjectedVariables = 16;
    public bool Distinct;
    public bool Reduced;
    public bool SelectAll;

    private int _aggregateCount;
    // Inline storage for up to 8 aggregate expressions
    private AggregateExpression _a0, _a1, _a2, _a3, _a4, _a5, _a6, _a7;

    // Inline storage for up to 16 projected variables (start, length pairs)
    private int _projectedVariableCount;
    private int _pv0s, _pv0l, _pv1s, _pv1l, _pv2s, _pv2l, _pv3s, _pv3l;
    private int _pv4s, _pv4l, _pv5s, _pv5l, _pv6s, _pv6l, _pv7s, _pv7l;
    private int _pv8s, _pv8l, _pv9s, _pv9l, _pv10s, _pv10l, _pv11s, _pv11l;
    private int _pv12s, _pv12l, _pv13s, _pv13l, _pv14s, _pv14l, _pv15s, _pv15l;

    public readonly int AggregateCount => _aggregateCount;
    public readonly bool HasAggregates => _aggregateCount > 0;
    /// <summary>
    /// Returns true if there are any real aggregate functions (COUNT, SUM, AVG, etc.).
    /// Non-aggregate computed expressions (HOURS, STR, etc.) with AggregateFunction.None
    /// do not count as real aggregates and should not trigger implicit grouping.
    /// </summary>
    public readonly bool HasRealAggregates
    {
        get
        {
            for (int i = 0; i < _aggregateCount; i++)
            {
                if (GetAggregate(i).Function != AggregateFunction.None)
                    return true;
            }
            return false;
        }
    }
    public readonly int ProjectedVariableCount => _projectedVariableCount;
    public readonly bool HasProjectedVariables => _projectedVariableCount > 0;

    public void AddAggregate(AggregateExpression agg)
    {
        if (_aggregateCount >= MaxAggregates)
            throw new SparqlParseException("Too many aggregate expressions (max 8)");

        switch (_aggregateCount)
        {
            case 0: _a0 = agg; break;
            case 1: _a1 = agg; break;
            case 2: _a2 = agg; break;
            case 3: _a3 = agg; break;
            case 4: _a4 = agg; break;
            case 5: _a5 = agg; break;
            case 6: _a6 = agg; break;
            case 7: _a7 = agg; break;
        }
        _aggregateCount++;
    }

    public readonly AggregateExpression GetAggregate(int index)
    {
        return index switch
        {
            0 => _a0,
            1 => _a1,
            2 => _a2,
            3 => _a3,
            4 => _a4,
            5 => _a5,
            6 => _a6,
            7 => _a7,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    public void AddProjectedVariable(int start, int length)
    {
        if (_projectedVariableCount >= MaxProjectedVariables)
            throw new SparqlParseException("Too many projected variables (max 16)");

        switch (_projectedVariableCount)
        {
            case 0: _pv0s = start; _pv0l = length; break;
            case 1: _pv1s = start; _pv1l = length; break;
            case 2: _pv2s = start; _pv2l = length; break;
            case 3: _pv3s = start; _pv3l = length; break;
            case 4: _pv4s = start; _pv4l = length; break;
            case 5: _pv5s = start; _pv5l = length; break;
            case 6: _pv6s = start; _pv6l = length; break;
            case 7: _pv7s = start; _pv7l = length; break;
            case 8: _pv8s = start; _pv8l = length; break;
            case 9: _pv9s = start; _pv9l = length; break;
            case 10: _pv10s = start; _pv10l = length; break;
            case 11: _pv11s = start; _pv11l = length; break;
            case 12: _pv12s = start; _pv12l = length; break;
            case 13: _pv13s = start; _pv13l = length; break;
            case 14: _pv14s = start; _pv14l = length; break;
            case 15: _pv15s = start; _pv15l = length; break;
        }
        _projectedVariableCount++;
    }

    public readonly (int Start, int Length) GetProjectedVariable(int index)
    {
        return index switch
        {
            0 => (_pv0s, _pv0l),
            1 => (_pv1s, _pv1l),
            2 => (_pv2s, _pv2l),
            3 => (_pv3s, _pv3l),
            4 => (_pv4s, _pv4l),
            5 => (_pv5s, _pv5l),
            6 => (_pv6s, _pv6l),
            7 => (_pv7s, _pv7l),
            8 => (_pv8s, _pv8l),
            9 => (_pv9s, _pv9l),
            10 => (_pv10s, _pv10l),
            11 => (_pv11s, _pv11l),
            12 => (_pv12s, _pv12l),
            13 => (_pv13s, _pv13l),
            14 => (_pv14s, _pv14l),
            15 => (_pv15s, _pv15l),
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }
}
