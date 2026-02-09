using System.Globalization;

namespace SkyOmega.Mercury.Sparql.Types;

/// <summary>
/// Binding table for variable bindings during query execution.
/// Zero-allocation design using stackalloc buffers.
/// </summary>
public ref struct BindingTable
{
    private Span<Binding> _bindings;
    private int _count;
    private Span<char> _stringBuffer;
    private int _stringOffset;

    public BindingTable(Span<Binding> storage)
    {
        _bindings = storage;
        _count = 0;
        _stringBuffer = Span<char>.Empty;
        _stringOffset = 0;
    }

    public BindingTable(Span<Binding> storage, Span<char> stringBuffer)
    {
        _bindings = storage;
        _count = 0;
        _stringBuffer = stringBuffer;
        _stringOffset = 0;
    }

    /// <summary>
    /// Bind an integer value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, long value)
    {
        if (_count >= _bindings.Length) return;

        // Format the value as string and store in buffer (must use InvariantCulture for RDF compliance)
        Span<char> temp = stackalloc char[24];
        if (!value.TryFormat(temp, out int written, default, CultureInfo.InvariantCulture))
            return;

        if (_stringOffset + written > _stringBuffer.Length) return;

        temp.Slice(0, written).CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Integer;
        binding.IntegerValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = written;
        binding.BindScopeDepth = -1; // From triple pattern, not BIND
        _stringOffset += written;
    }

    /// <summary>
    /// Bind a double value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, double value)
    {
        if (_count >= _bindings.Length) return;

        // Format the value as string and store in buffer (must use InvariantCulture for RDF compliance)
        Span<char> temp = stackalloc char[32];
        if (!value.TryFormat(temp, out int written, default, CultureInfo.InvariantCulture))
            return;

        if (_stringOffset + written > _stringBuffer.Length) return;

        temp.Slice(0, written).CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Double;
        binding.DoubleValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = written;
        binding.BindScopeDepth = -1; // From triple pattern, not BIND
        _stringOffset += written;
    }

    /// <summary>
    /// Bind a boolean value to a variable.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, bool value)
    {
        if (_count >= _bindings.Length) return;

        // Store string representation
        var str = value ? "true" : "false";
        var len = str.Length;
        if (_stringOffset + len > _stringBuffer.Length) return;

        str.AsSpan().CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Boolean;
        binding.BooleanValue = value;
        binding.StringOffset = _stringOffset;
        binding.StringLength = len;
        binding.BindScopeDepth = -1; // From triple pattern, not BIND
        _stringOffset += len;
    }

    /// <summary>
    /// Bind a string value to a variable.
    /// Copies the string into the internal buffer.
    /// </summary>
    public void Bind(ReadOnlySpan<char> variableName, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.String;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;
        binding.BindScopeDepth = -1; // From triple pattern, not BIND

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Bind a URI value to a variable.
    /// Copies the URI into the internal buffer.
    /// </summary>
    public void BindUri(ReadOnlySpan<char> variableName, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = ComputeHash(variableName);
        binding.Type = BindingValueType.Uri;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;
        binding.BindScopeDepth = -1; // From triple pattern, not BIND

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Bind a string value using a pre-computed hash.
    /// Used for ORDER BY result reconstruction.
    /// </summary>
    public void BindWithHash(int variableNameHash, ReadOnlySpan<char> value)
    {
        if (_count >= _bindings.Length) return;
        if (_stringOffset + value.Length > _stringBuffer.Length) return;

        // Copy string to buffer
        value.CopyTo(_stringBuffer.Slice(_stringOffset));

        ref var binding = ref _bindings[_count++];
        binding.VariableNameHash = variableNameHash;
        binding.Type = BindingValueType.String;
        binding.StringOffset = _stringOffset;
        binding.StringLength = value.Length;
        binding.BindScopeDepth = -1; // From triple pattern, not BIND

        _stringOffset += value.Length;
    }

    /// <summary>
    /// Set the scope depth of the last added binding.
    /// Call this after Bind() when adding a binding from a BIND expression.
    /// </summary>
    /// <param name="scopeDepth">The scope depth of the BIND expression</param>
    public void SetLastBindScopeDepth(int scopeDepth)
    {
        if (_count > 0)
        {
            _bindings[_count - 1].BindScopeDepth = scopeDepth;
        }
    }

    /// <summary>
    /// Try to get the binding for a variable.
    /// Returns the index if found, -1 otherwise.
    /// </summary>
    public readonly int FindBinding(ReadOnlySpan<char> variableName)
    {
        var hash = ComputeHash(variableName);
        for (int i = 0; i < _count; i++)
        {
            if (_bindings[i].VariableNameHash == hash)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Find the index of a binding by its pre-computed hash.
    /// </summary>
    public readonly int FindBindingByHash(int hash)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_bindings[i].VariableNameHash == hash)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Get the type of the binding at the given index.
    /// </summary>
    public readonly BindingValueType GetType(int index) => _bindings[index].Type;

    /// <summary>
    /// Get the integer value at the given index.
    /// </summary>
    public readonly long GetInteger(int index) => _bindings[index].IntegerValue;

    /// <summary>
    /// Get the double value at the given index.
    /// </summary>
    public readonly double GetDouble(int index) => _bindings[index].DoubleValue;

    /// <summary>
    /// Get the boolean value at the given index.
    /// </summary>
    public readonly bool GetBoolean(int index) => _bindings[index].BooleanValue;

    /// <summary>
    /// Get the string value at the given index.
    /// </summary>
    public readonly ReadOnlySpan<char> GetString(int index)
    {
        ref readonly var binding = ref _bindings[index];
        return _stringBuffer.Slice(binding.StringOffset, binding.StringLength);
    }

    /// <summary>
    /// Get the variable name hash at the given index.
    /// </summary>
    public readonly int GetVariableHash(int index) => _bindings[index].VariableNameHash;

    /// <summary>
    /// Clear all bindings for reuse with next row.
    /// </summary>
    public void Clear()
    {
        _count = 0;
        _stringOffset = 0;
    }

    /// <summary>
    /// Truncate bindings to a previous count.
    /// Used for backtracking in multi-pattern joins.
    /// Reclaims string buffer space to prevent overflow during heavy backtracking.
    /// </summary>
    public void TruncateTo(int count)
    {
        if (count < _count)
        {
            // Reclaim string buffer space by resetting to right after the last retained binding
            // Each binding stores its StringOffset and StringLength, and bindings are added
            // in order, so string space is allocated sequentially.
            if (count > 0)
            {
                ref var lastBinding = ref _bindings[count - 1];
                _stringOffset = lastBinding.StringOffset + lastBinding.StringLength;
            }
            else
            {
                _stringOffset = 0;
            }
            _count = count;
        }
    }

    /// <summary>
    /// Number of bound variables.
    /// </summary>
    public readonly int Count => _count;

    /// <summary>
    /// Get the raw binding data for direct access.
    /// </summary>
    public readonly ReadOnlySpan<Binding> GetBindings() => _bindings.Slice(0, _count);

    /// <summary>
    /// Get the string buffer for direct access.
    /// </summary>
    public readonly ReadOnlySpan<char> GetStringBuffer() => _stringBuffer.Slice(0, _stringOffset);

    /// <summary>
    /// Get a binding at the specified index.
    /// </summary>
    public readonly Binding Get(int index) => _bindings[index];

    /// <summary>
    /// Get the current length of the string buffer (bytes used).
    /// </summary>
    public readonly int StringBufferLength => _stringOffset;

    /// <summary>
    /// Copy the string buffer contents to a destination span.
    /// </summary>
    public readonly void CopyStringsTo(Span<char> destination)
    {
        _stringBuffer.Slice(0, _stringOffset).CopyTo(destination);
    }

    private static int ComputeHash(ReadOnlySpan<char> value)
    {
        // FNV-1a hash
        uint hash = 2166136261;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return (int)hash;
    }
}
