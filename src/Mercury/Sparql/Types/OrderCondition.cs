namespace SkyOmega.Mercury.Sparql.Types;

public readonly struct OrderCondition
{
    public readonly int VariableStart;
    public readonly int VariableLength;
    public readonly OrderDirection Direction;

    public OrderCondition(int start, int length, OrderDirection direction)
    {
        VariableStart = start;
        VariableLength = length;
        Direction = direction;
    }
}
