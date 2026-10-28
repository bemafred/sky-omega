namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// [23] OrderCondition: the span of its variable or of its expression (a BrackettedExpression without its brackets, or
/// a built-in or function call), and its direction.
/// </summary>
internal readonly struct OrderCondition
{
    public readonly int ExpressionStart;
    public readonly int ExpressionLength;
    public readonly OrderDirection Direction;

    public OrderCondition(int start, int length, OrderDirection direction)
    {
        ExpressionStart = start;
        ExpressionLength = length;
        Direction = direction;
    }
}
