namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// A VALUES clause: VALUES ?var { value1 ... } or VALUES (?var1 ?var2) { (val1 val2) ... }
/// Supports up to 4 variables and up to 16 total values (stored in row-major order).
/// UNDEF values are marked with length = -1.
/// </summary>
internal struct ValuesClause
{
    public const int MaxVariables = 4;
    public const int MaxValues = 16;

    // Variable storage (up to 4 variables)
    public int VarStart;     // Start of first variable name (including ?) - for backwards compatibility
    public int VarLength;    // Length of first variable name - for backwards compatibility
    private int _var1Start, _var1Len, _var2Start, _var2Len, _var3Start, _var3Len;
    private int _varCount;

    private int _valueCount;

    // Inline storage for value offsets (16 values * 2 ints = 128 bytes)
    private int _v0Start, _v0Len, _v1Start, _v1Len, _v2Start, _v2Len, _v3Start, _v3Len;
    private int _v4Start, _v4Len, _v5Start, _v5Len, _v6Start, _v6Len, _v7Start, _v7Len;
    private int _v8Start, _v8Len, _v9Start, _v9Len, _v10Start, _v10Len, _v11Start, _v11Len;
    private int _v12Start, _v12Len, _v13Start, _v13Len, _v14Start, _v14Len, _v15Start, _v15Len;

    public readonly int ValueCount => _valueCount;
    public readonly int VariableCount => _varCount;
    public readonly bool HasValues => _valueCount > 0;
    public readonly int RowCount => _varCount > 0 ? _valueCount / _varCount : 0;

    /// <summary>
    /// Add a variable to the VALUES clause.
    /// </summary>
    public void AddVariable(int start, int length)
    {
        if (_varCount >= MaxVariables) return;
        SetVariable(_varCount++, start, length);
    }

    /// <summary>
    /// Get variable by index.
    /// </summary>
    public readonly (int Start, int Length) GetVariable(int index)
    {
        return index switch
        {
            0 => (VarStart, VarLength),
            1 => (_var1Start, _var1Len),
            2 => (_var2Start, _var2Len),
            3 => (_var3Start, _var3Len),
            _ => (0, 0)
        };
    }

    private void SetVariable(int index, int start, int length)
    {
        switch (index)
        {
            case 0: VarStart = start; VarLength = length; break;
            case 1: _var1Start = start; _var1Len = length; break;
            case 2: _var2Start = start; _var2Len = length; break;
            case 3: _var3Start = start; _var3Len = length; break;
        }
    }

    /// <summary>
    /// Add a value to the VALUES clause. Use length = -1 for UNDEF.
    /// Values are stored in row-major order.
    /// </summary>
    public void AddValue(int start, int length)
    {
        if (_valueCount >= MaxValues) return;
        SetValue(_valueCount++, start, length);
    }

    /// <summary>
    /// Check if a value is UNDEF (length = -1).
    /// </summary>
    public readonly bool IsUndef(int index)
    {
        var (_, length) = GetValue(index);
        return length == -1;
    }

    /// <summary>
    /// Get value at a specific row and column (variable index).
    /// </summary>
    public readonly (int Start, int Length) GetValueAt(int row, int varIndex)
    {
        if (_varCount == 0) return (0, 0);
        var index = row * _varCount + varIndex;
        return GetValue(index);
    }

    public readonly (int Start, int Length) GetValue(int index)
    {
        return index switch
        {
            0 => (_v0Start, _v0Len), 1 => (_v1Start, _v1Len),
            2 => (_v2Start, _v2Len), 3 => (_v3Start, _v3Len),
            4 => (_v4Start, _v4Len), 5 => (_v5Start, _v5Len),
            6 => (_v6Start, _v6Len), 7 => (_v7Start, _v7Len),
            8 => (_v8Start, _v8Len), 9 => (_v9Start, _v9Len),
            10 => (_v10Start, _v10Len), 11 => (_v11Start, _v11Len),
            12 => (_v12Start, _v12Len), 13 => (_v13Start, _v13Len),
            14 => (_v14Start, _v14Len), 15 => (_v15Start, _v15Len),
            _ => (0, 0)
        };
    }

    private void SetValue(int index, int start, int length)
    {
        switch (index)
        {
            case 0: _v0Start = start; _v0Len = length; break;
            case 1: _v1Start = start; _v1Len = length; break;
            case 2: _v2Start = start; _v2Len = length; break;
            case 3: _v3Start = start; _v3Len = length; break;
            case 4: _v4Start = start; _v4Len = length; break;
            case 5: _v5Start = start; _v5Len = length; break;
            case 6: _v6Start = start; _v6Len = length; break;
            case 7: _v7Start = start; _v7Len = length; break;
            case 8: _v8Start = start; _v8Len = length; break;
            case 9: _v9Start = start; _v9Len = length; break;
            case 10: _v10Start = start; _v10Len = length; break;
            case 11: _v11Start = start; _v11Len = length; break;
            case 12: _v12Start = start; _v12Len = length; break;
            case 13: _v13Start = start; _v13Len = length; break;
            case 14: _v14Start = start; _v14Len = length; break;
            case 15: _v15Start = start; _v15Len = length; break;
        }
    }
}
