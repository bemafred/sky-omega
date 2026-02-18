namespace SkyOmega.Mercury.Sparql.Types;

internal struct OrderByClause
{
    // Store up to 4 order conditions inline
    private OrderCondition _cond0, _cond1, _cond2, _cond3;
    private int _count;

    public readonly int Count => _count;
    public readonly bool HasOrderBy => _count > 0;

    public void AddCondition(int variableStart, int variableLength, OrderDirection direction)
    {
        var cond = new OrderCondition(variableStart, variableLength, direction);
        switch (_count)
        {
            case 0: _cond0 = cond; break;
            case 1: _cond1 = cond; break;
            case 2: _cond2 = cond; break;
            case 3: _cond3 = cond; break;
            default: return; // Ignore beyond 4
        }
        _count++;
    }

    public readonly OrderCondition GetCondition(int index)
    {
        return index switch
        {
            0 => _cond0,
            1 => _cond1,
            2 => _cond2,
            3 => _cond3,
            _ => default
        };
    }
}
