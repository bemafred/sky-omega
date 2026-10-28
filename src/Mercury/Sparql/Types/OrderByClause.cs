using System;

namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// [22] OrderClause ::= 'ORDER' 'BY' OrderCondition+: the conditions in order, with no size limit. Read once per query
/// at parse time, so its storage is an ordinary array that grows as conditions are added.
/// </summary>
internal struct OrderByClause
{
    private OrderCondition[]? _conditions;
    private int _count;

    public readonly int Count => _count;
    public readonly bool HasOrderBy => _count > 0;

    public void AddCondition(int expressionStart, int expressionLength, OrderDirection direction)
    {
        if (_conditions == null || _count == _conditions.Length)
            Array.Resize(ref _conditions, Math.Max(4, 2 * _count));
        _conditions[_count++] = new OrderCondition(expressionStart, expressionLength, direction);
    }

    public readonly OrderCondition GetCondition(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _conditions![index];
    }
}
