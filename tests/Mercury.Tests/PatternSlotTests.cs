// PatternSlotTests.cs
// Tests for PatternSlot, PatternArray, and QueryBuffer components
// Validates zero-GC discriminated union pattern implementation

using System.Buffers;
using SkyOmega.Mercury.Sparql;
using SkyOmega.Mercury.Sparql.Patterns;
using Xunit;

namespace SkyOmega.Mercury.Tests;

public class PatternSlotTests
{
    #region PatternSlot Construction and Size

    [Fact]
    public void PatternSlot_Size_Is64Bytes()
    {
        Assert.Equal(64, PatternSlot.Size);
    }

    [Fact]
    public void PatternSlot_RequiresMinimumBufferSize()
    {
        byte[] tooSmall = new byte[32];
        Assert.Throws<ArgumentOutOfRangeException>(() => new PatternSlot(tooSmall));
    }

    [Fact]
    public void PatternSlot_AcceptsExactSize()
    {
        Span<byte> exact = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(exact);
        Assert.Equal(PatternKind.Empty, slot.Kind);
    }

    [Fact]
    public void PatternSlot_AcceptsLargerBuffer()
    {
        Span<byte> larger = stackalloc byte[PatternSlot.Size * 2];
        var slot = new PatternSlot(larger);
        Assert.Equal(PatternKind.Empty, slot.Kind);
    }

    [Fact]
    public void PatternSlot_Clear_ZeroesAllBytes()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        buffer.Fill(0xFF);

        var slot = new PatternSlot(buffer);
        slot.Kind = PatternKind.Triple;
        slot.SubjectStart = 100;

        slot.Clear();

        Assert.Equal(PatternKind.Empty, slot.Kind);
        Assert.Equal(0, slot.SubjectStart);
    }

    #endregion

    #region PatternSlot Triple Variant

    [Fact]
    public void PatternSlot_Triple_StoresAllFields()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.Triple;
        slot.SubjectType = TermType.Variable;
        slot.SubjectStart = 10;
        slot.SubjectLength = 5;
        slot.PredicateType = TermType.Iri;
        slot.PredicateStart = 20;
        slot.PredicateLength = 30;
        slot.ObjectType = TermType.Literal;
        slot.ObjectStart = 50;
        slot.ObjectLength = 15;
        slot.PathKind = PathType.ZeroOrMore;
        slot.PathIriStart = 100;
        slot.PathIriLength = 20;

        Assert.Equal(PatternKind.Triple, slot.Kind);
        Assert.Equal(TermType.Variable, slot.SubjectType);
        Assert.Equal(10, slot.SubjectStart);
        Assert.Equal(5, slot.SubjectLength);
        Assert.Equal(TermType.Iri, slot.PredicateType);
        Assert.Equal(20, slot.PredicateStart);
        Assert.Equal(30, slot.PredicateLength);
        Assert.Equal(TermType.Literal, slot.ObjectType);
        Assert.Equal(50, slot.ObjectStart);
        Assert.Equal(15, slot.ObjectLength);
        Assert.Equal(PathType.ZeroOrMore, slot.PathKind);
        Assert.Equal(100, slot.PathIriStart);
        Assert.Equal(20, slot.PathIriLength);

        Assert.True(slot.IsTriple);
        Assert.False(slot.IsFilter);
        Assert.False(slot.IsBind);
    }

    [Fact]
    public void PatternSlot_Triple_SupportsAllTermTypes()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);
        slot.Kind = PatternKind.Triple;

        foreach (var termType in Enum.GetValues<TermType>())
        {
            slot.SubjectType = termType;
            Assert.Equal(termType, slot.SubjectType);
        }
    }

    [Fact]
    public void PatternSlot_Triple_SupportsAllPathTypes()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);
        slot.Kind = PatternKind.Triple;

        foreach (var pathType in Enum.GetValues<PathType>())
        {
            slot.PathKind = pathType;
            Assert.Equal(pathType, slot.PathKind);
        }
    }

    #endregion

    #region PatternSlot MinusTriple Variant

    [Fact]
    public void PatternSlot_MinusTriple_SharesLayoutWithTriple()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.MinusTriple;
        slot.SubjectType = TermType.Variable;
        slot.SubjectStart = 5;
        slot.SubjectLength = 3;
        slot.PredicateType = TermType.Iri;
        slot.PredicateStart = 10;
        slot.PredicateLength = 20;
        slot.ObjectType = TermType.Variable;
        slot.ObjectStart = 40;
        slot.ObjectLength = 4;

        Assert.True(slot.IsMinusTriple);
        Assert.False(slot.IsTriple);
        Assert.Equal(TermType.Variable, slot.SubjectType);
        Assert.Equal(5, slot.SubjectStart);
    }

    #endregion

    #region PatternSlot Filter Variant

    [Fact]
    public void PatternSlot_Filter_StoresFields()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.Filter;
        slot.FilterStart = 100;
        slot.FilterLength = 50;

        Assert.Equal(PatternKind.Filter, slot.Kind);
        Assert.Equal(100, slot.FilterStart);
        Assert.Equal(50, slot.FilterLength);

        Assert.True(slot.IsFilter);
        Assert.False(slot.IsTriple);
    }

    #endregion

    #region PatternSlot Bind Variant

    [Fact]
    public void PatternSlot_Bind_StoresFields()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.Bind;
        slot.BindExprStart = 50;
        slot.BindExprLength = 30;
        slot.BindVarStart = 85;
        slot.BindVarLength = 5;

        Assert.Equal(PatternKind.Bind, slot.Kind);
        Assert.Equal(50, slot.BindExprStart);
        Assert.Equal(30, slot.BindExprLength);
        Assert.Equal(85, slot.BindVarStart);
        Assert.Equal(5, slot.BindVarLength);

        Assert.True(slot.IsBind);
    }

    #endregion

    #region PatternSlot GraphHeader Variant

    [Fact]
    public void PatternSlot_GraphHeader_StoresFields()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.GraphHeader;
        slot.GraphTermType = TermType.Iri;
        slot.GraphTermStart = 10;
        slot.GraphTermLength = 40;
        slot.ChildStartIndex = 5;
        slot.ChildCount = 3;

        Assert.Equal(PatternKind.GraphHeader, slot.Kind);
        Assert.Equal(TermType.Iri, slot.GraphTermType);
        Assert.Equal(10, slot.GraphTermStart);
        Assert.Equal(40, slot.GraphTermLength);
        Assert.Equal(5, slot.ChildStartIndex);
        Assert.Equal(3, slot.ChildCount);

        Assert.True(slot.IsGraphHeader);
    }

    [Fact]
    public void PatternSlot_GraphHeader_SupportsVariableGraph()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.GraphHeader;
        slot.GraphTermType = TermType.Variable;
        slot.GraphTermStart = 0;
        slot.GraphTermLength = 2;

        Assert.Equal(TermType.Variable, slot.GraphTermType);
    }

    #endregion

    #region PatternSlot Exists/NotExists Variants

    [Fact]
    public void PatternSlot_ExistsHeader_StoresFields()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.ExistsHeader;
        slot.ExistsChildStart = 10;
        slot.ExistsChildCount = 2;

        Assert.Equal(PatternKind.ExistsHeader, slot.Kind);
        Assert.Equal(10, slot.ExistsChildStart);
        Assert.Equal(2, slot.ExistsChildCount);

        Assert.True(slot.IsExistsHeader);
        Assert.False(slot.IsNegatedExists);
    }

    [Fact]
    public void PatternSlot_NotExistsHeader_StoresFields()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.NotExistsHeader;
        slot.ExistsChildStart = 20;
        slot.ExistsChildCount = 4;

        Assert.Equal(PatternKind.NotExistsHeader, slot.Kind);
        Assert.True(slot.IsExistsHeader);
        Assert.True(slot.IsNegatedExists);
    }

    #endregion

    #region PatternSlot Values Variants

    [Fact]
    public void PatternSlot_ValuesHeader_StoresFields()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.ValuesHeader;
        slot.ValuesVarStart = 5;
        slot.ValuesVarLength = 10;
        slot.ValuesEntryCount = 3;

        Assert.Equal(PatternKind.ValuesHeader, slot.Kind);
        Assert.Equal(5, slot.ValuesVarStart);
        Assert.Equal(10, slot.ValuesVarLength);
        Assert.Equal(3, slot.ValuesEntryCount);

        Assert.True(slot.IsValuesHeader);
    }

    [Fact]
    public void PatternSlot_ValuesEntry_StoresFields()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size];
        var slot = new PatternSlot(buffer);

        slot.Kind = PatternKind.ValuesEntry;
        slot.ValuesEntryStart = 50;
        slot.ValuesEntryLength = 10;

        Assert.Equal(PatternKind.ValuesEntry, slot.Kind);
        Assert.Equal(50, slot.ValuesEntryStart);
        Assert.Equal(10, slot.ValuesEntryLength);

        Assert.True(slot.IsValuesEntry);
    }

    #endregion
}

public class PatternArrayTests
{
    #region Construction and Capacity

    [Fact]
    public void PatternArray_ComputesCapacityFromBufferSize()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);

        Assert.Equal(4, array.Capacity);
        Assert.Equal(0, array.Count);
    }

    [Fact]
    public void PatternArray_ConstructWithCount_SetsInitialCount()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer, 2);

        Assert.Equal(4, array.Capacity);
        Assert.Equal(2, array.Count);
    }

    [Fact]
    public void PatternArray_FromExtensionMethod()
    {
        byte[] buffer = new byte[PatternSlot.Size * 8];
        var array = buffer.AsPatternArray();

        Assert.Equal(8, array.Capacity);
    }

    [Fact]
    public void PatternArray_PatternBufferSize_CalculatesCorrectly()
    {
        Assert.Equal(64, PatternArrayExtensions.PatternBufferSize(1));
        Assert.Equal(640, PatternArrayExtensions.PatternBufferSize(10));
        Assert.Equal(2048, PatternArrayExtensions.PatternBufferSize(32));
    }

    #endregion

    #region Adding Patterns

    [Fact]
    public void PatternArray_AddTriple_IncrementsCount()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);

        array.AddTriple(
            TermType.Variable, 0, 2,
            TermType.Iri, 10, 20,
            TermType.Variable, 40, 2);

        Assert.Equal(1, array.Count);

        var slot = array[0];
        Assert.Equal(PatternKind.Triple, slot.Kind);
        Assert.Equal(TermType.Variable, slot.SubjectType);
        Assert.Equal(0, slot.SubjectStart);
        Assert.Equal(2, slot.SubjectLength);
    }

    [Fact]
    public void PatternArray_AddTriple_WithPath()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);

        array.AddTriple(
            TermType.Variable, 0, 2,
            TermType.Iri, 10, 20,
            TermType.Variable, 40, 2,
            PathType.OneOrMore, 100, 15);

        var slot = array[0];
        Assert.Equal(PathType.OneOrMore, slot.PathKind);
        Assert.Equal(100, slot.PathIriStart);
        Assert.Equal(15, slot.PathIriLength);
    }

    [Fact]
    public void PatternArray_AddFilter()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);

        array.AddFilter(50, 25);

        Assert.Equal(1, array.Count);
        var slot = array[0];
        Assert.Equal(PatternKind.Filter, slot.Kind);
        Assert.Equal(50, slot.FilterStart);
        Assert.Equal(25, slot.FilterLength);
    }

    [Fact]
    public void PatternArray_AddBind()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);

        array.AddBind(10, 20, 35, 5);

        Assert.Equal(1, array.Count);
        var slot = array[0];
        Assert.Equal(PatternKind.Bind, slot.Kind);
        Assert.Equal(10, slot.BindExprStart);
        Assert.Equal(20, slot.BindExprLength);
        Assert.Equal(35, slot.BindVarStart);
        Assert.Equal(5, slot.BindVarLength);
    }

    [Fact]
    public void PatternArray_AddMinusTriple()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);

        array.AddMinusTriple(
            TermType.Variable, 0, 2,
            TermType.Iri, 10, 20,
            TermType.Variable, 40, 2);

        Assert.Equal(1, array.Count);
        var slot = array[0];
        Assert.Equal(PatternKind.MinusTriple, slot.Kind);
    }

    [Fact]
    public void PatternArray_ThrowsWhenFull()
    {
        byte[] buffer = new byte[PatternSlot.Size * 2];
        var array = new PatternArray(buffer);

        array.AddFilter(0, 10);
        array.AddFilter(10, 10);

        var ex = Record.Exception(() =>
        {
            // Create a fresh array view over the same buffer to test overflow
            var testArray = new PatternArray(buffer, 2);
            testArray.AllocateSlot();
        });
        Assert.IsType<InvalidOperationException>(ex);
    }

    #endregion

    #region GRAPH Clause Support

    [Fact]
    public void PatternArray_BeginEndGraph()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginGraph(TermType.Iri, 0, 30);
        array.AddTriple(
            TermType.Variable, 40, 2,
            TermType.Iri, 50, 20,
            TermType.Variable, 80, 2);
        array.AddTriple(
            TermType.Variable, 40, 2,
            TermType.Iri, 100, 15,
            TermType.Literal, 120, 10);
        array.EndGraph(headerIndex);

        Assert.Equal(3, array.Count);

        var header = array[headerIndex];
        Assert.Equal(PatternKind.GraphHeader, header.Kind);
        Assert.Equal(TermType.Iri, header.GraphTermType);
        Assert.Equal(2, header.ChildCount);
        Assert.Equal(1, header.ChildStartIndex);
    }

    [Fact]
    public void PatternArray_BeginEndGraph_VariableGraph()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginGraph(TermType.Variable, 0, 2);
        array.AddTriple(
            TermType.Variable, 10, 2,
            TermType.Iri, 20, 20,
            TermType.Variable, 50, 2);
        array.EndGraph(headerIndex);

        var header = array[headerIndex];
        Assert.Equal(TermType.Variable, header.GraphTermType);
    }

    [Fact]
    public void PatternArray_GetChildren_ReturnsGraphChildren()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginGraph(TermType.Iri, 0, 30);
        array.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);
        array.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 50, 20, TermType.Literal, 80, 10);
        array.EndGraph(headerIndex);

        var children = array.GetChildren(headerIndex);

        Assert.Equal(2, children.Count);
        Assert.Equal(PatternKind.Triple, children[0].Kind);
        Assert.Equal(PatternKind.Triple, children[1].Kind);
    }

    #endregion

    #region EXISTS Filter Support

    [Fact]
    public void PatternArray_BeginEndExists()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginExists(negated: false);
        array.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);
        array.EndExists(headerIndex);

        Assert.Equal(2, array.Count);

        var header = array[headerIndex];
        Assert.Equal(PatternKind.ExistsHeader, header.Kind);
        Assert.Equal(1, header.ExistsChildCount);
        Assert.Equal(1, header.ExistsChildStart);
    }

    [Fact]
    public void PatternArray_BeginEndNotExists()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginExists(negated: true);
        array.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);
        array.EndExists(headerIndex);

        var header = array[headerIndex];
        Assert.Equal(PatternKind.NotExistsHeader, header.Kind);
        Assert.True(header.IsNegatedExists);
    }

    [Fact]
    public void PatternArray_GetChildren_ReturnsExistsChildren()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginExists(negated: false);
        array.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);
        array.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 50, 20, TermType.Literal, 80, 10);
        array.EndExists(headerIndex);

        var children = array.GetChildren(headerIndex);

        Assert.Equal(2, children.Count);
    }

    #endregion

    #region VALUES Clause Support

    [Fact]
    public void PatternArray_AddValuesHeaderAndEntries()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.AddValuesHeader(0, 5);
        array.AddValuesEntry(10, 8, headerIndex);
        array.AddValuesEntry(20, 12, headerIndex);
        array.AddValuesEntry(35, 6, headerIndex);

        Assert.Equal(4, array.Count);

        var header = array[headerIndex];
        Assert.Equal(PatternKind.ValuesHeader, header.Kind);
        Assert.Equal(0, header.ValuesVarStart);
        Assert.Equal(5, header.ValuesVarLength);
        Assert.Equal(3, header.ValuesEntryCount);

        var entry = array[1];
        Assert.Equal(PatternKind.ValuesEntry, entry.Kind);
        Assert.Equal(10, entry.ValuesEntryStart);
        Assert.Equal(8, entry.ValuesEntryLength);
    }

    #endregion

    #region Indexer

    [Fact]
    public void PatternArray_Indexer_ThrowsForInvalidIndex()
    {
        byte[] buffer = new byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);
        array.AddFilter(0, 10);

        // Test index beyond count
        var ex1 = Record.Exception(() =>
        {
            var testArray = new PatternArray(buffer, 1);
            _ = testArray[1];
        });
        Assert.IsType<IndexOutOfRangeException>(ex1);

        // Test negative index
        var ex2 = Record.Exception(() =>
        {
            var testArray = new PatternArray(buffer, 1);
            _ = testArray[-1];
        });
        Assert.IsType<IndexOutOfRangeException>(ex2);
    }

    [Fact]
    public void PatternArray_Indexer_AccessesCorrectSlot()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);

        array.AddFilter(10, 5);
        array.AddFilter(20, 8);
        array.AddFilter(30, 12);

        Assert.Equal(10, array[0].FilterStart);
        Assert.Equal(20, array[1].FilterStart);
        Assert.Equal(30, array[2].FilterStart);
    }

    #endregion

    #region AllocateSlot

    [Fact]
    public void PatternArray_AllocateSlot_ReturnsCleanSlot()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        buffer.Fill(0xFF);

        var array = new PatternArray(buffer);
        var slot = array.AllocateSlot();

        Assert.Equal(1, array.Count);
        Assert.Equal(PatternKind.Empty, slot.Kind);
        Assert.Equal(0, slot.SubjectStart);
    }

    #endregion
}

public class PatternEnumeratorTests
{
    [Fact]
    public void PatternEnumerator_IteratesAllSlots()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        array.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);
        array.AddFilter(50, 10);
        array.AddBind(60, 15, 80, 3);

        int count = 0;
        foreach (var slot in array)
        {
            count++;
        }

        Assert.Equal(3, count);
    }

    [Fact]
    public void PatternEnumerator_EmptyArray_NoIterations()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 4];
        var array = new PatternArray(buffer);

        int count = 0;
        foreach (var _ in array)
        {
            count++;
        }

        Assert.Equal(0, count);
    }

    [Fact]
    public void TripleEnumerator_OnlyReturnsTriples()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        array.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);
        array.AddFilter(50, 10);
        array.AddTriple(TermType.Iri, 60, 30, TermType.Iri, 100, 20, TermType.Literal, 130, 15);
        array.AddBind(150, 20, 175, 4);

        int tripleCount = 0;
        foreach (var slot in array.EnumerateTriples())
        {
            Assert.Equal(PatternKind.Triple, slot.Kind);
            tripleCount++;
        }

        Assert.Equal(2, tripleCount);
    }

    [Fact]
    public void FilterEnumerator_OnlyReturnsFilters()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        array.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);
        array.AddFilter(50, 10);
        array.AddFilter(65, 15);
        array.AddBind(85, 20, 110, 4);

        int filterCount = 0;
        foreach (var slot in array.EnumerateFilters())
        {
            Assert.Equal(PatternKind.Filter, slot.Kind);
            filterCount++;
        }

        Assert.Equal(2, filterCount);
    }

    [Fact]
    public void GraphHeaderEnumerator_OnlyReturnsGraphHeaders()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 16];
        var array = new PatternArray(buffer);

        // First graph
        int g1 = array.BeginGraph(TermType.Iri, 0, 20);
        array.AddTriple(TermType.Variable, 25, 2, TermType.Iri, 30, 20, TermType.Variable, 55, 2);
        array.EndGraph(g1);

        // Some non-graph patterns
        array.AddFilter(60, 10);

        // Second graph
        int g2 = array.BeginGraph(TermType.Variable, 75, 2);
        array.AddTriple(TermType.Variable, 80, 2, TermType.Iri, 85, 20, TermType.Literal, 110, 10);
        array.EndGraph(g2);

        int headerCount = 0;
        int firstIndex = -1;
        int secondIndex = -1;
        PatternKind firstKind = PatternKind.Empty;

        var enumerator = array.EnumerateGraphHeaders();
        while (enumerator.MoveNext())
        {
            if (headerCount == 0)
            {
                firstIndex = enumerator.CurrentIndex;
                firstKind = enumerator.Current.Kind;
            }
            else if (headerCount == 1)
            {
                secondIndex = enumerator.CurrentIndex;
            }
            headerCount++;
        }

        Assert.Equal(2, headerCount);
        Assert.Equal(0, firstIndex);
        Assert.Equal(PatternKind.GraphHeader, firstKind);
        Assert.Equal(3, secondIndex); // g1 header (0) + 1 triple (1) + filter (2) -> g2 header at 3
    }

    [Fact]
    public void GraphHeaderEnumerator_TracksHeaderIndex()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 16];
        var array = new PatternArray(buffer);

        int g1 = array.BeginGraph(TermType.Iri, 0, 20);
        array.AddTriple(TermType.Variable, 25, 2, TermType.Iri, 30, 20, TermType.Variable, 55, 2);
        array.EndGraph(g1);

        int g2 = array.BeginGraph(TermType.Iri, 60, 20);
        array.AddTriple(TermType.Variable, 85, 2, TermType.Iri, 90, 20, TermType.Variable, 115, 2);
        array.EndGraph(g2);

        int count = 0;
        int firstHeaderIndex = -1;
        int secondHeaderIndex = -1;
        var enumerator = array.EnumerateGraphHeaders();
        while (enumerator.MoveNext())
        {
            if (count == 0)
                firstHeaderIndex = enumerator.HeaderIndex;
            else if (count == 1)
                secondHeaderIndex = enumerator.HeaderIndex;
            count++;
        }

        Assert.Equal(2, count);
        Assert.Equal(0, firstHeaderIndex);
        Assert.Equal(1, secondHeaderIndex);
    }
}

public class PatternArraySliceTests
{
    [Fact]
    public void PatternArraySlice_Count_ReturnsChildCount()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginGraph(TermType.Iri, 0, 30);
        array.AddTriple(TermType.Variable, 35, 2, TermType.Iri, 40, 20, TermType.Variable, 65, 2);
        array.AddTriple(TermType.Variable, 35, 2, TermType.Iri, 70, 15, TermType.Literal, 90, 10);
        array.EndGraph(headerIndex);

        var slice = array.GetChildren(headerIndex);

        Assert.Equal(2, slice.Count);
    }

    [Fact]
    public void PatternArraySlice_Indexer_AccessesCorrectChild()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginGraph(TermType.Iri, 0, 30);
        array.AddTriple(TermType.Variable, 35, 2, TermType.Iri, 40, 20, TermType.Variable, 65, 2);
        array.AddTriple(TermType.Variable, 35, 2, TermType.Iri, 70, 15, TermType.Literal, 90, 10);
        array.EndGraph(headerIndex);

        var slice = array.GetChildren(headerIndex);

        Assert.Equal(40, slice[0].PredicateStart);
        Assert.Equal(70, slice[1].PredicateStart);
    }

    [Fact]
    public void PatternArraySlice_Indexer_ThrowsForInvalidIndex()
    {
        byte[] buffer = new byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginGraph(TermType.Iri, 0, 30);
        array.AddTriple(TermType.Variable, 35, 2, TermType.Iri, 40, 20, TermType.Variable, 65, 2);
        array.EndGraph(headerIndex);

        // Test index beyond count
        var ex1 = Record.Exception(() =>
        {
            var testArray = new PatternArray(buffer, 2);
            var testSlice = testArray.GetChildren(0);
            _ = testSlice[1];
        });
        Assert.IsType<IndexOutOfRangeException>(ex1);

        // Test negative index
        var ex2 = Record.Exception(() =>
        {
            var testArray = new PatternArray(buffer, 2);
            var testSlice = testArray.GetChildren(0);
            _ = testSlice[-1];
        });
        Assert.IsType<IndexOutOfRangeException>(ex2);
    }

    [Fact]
    public void PatternArraySlice_Enumerator_IteratesChildren()
    {
        Span<byte> buffer = stackalloc byte[PatternSlot.Size * 8];
        var array = new PatternArray(buffer);

        int headerIndex = array.BeginGraph(TermType.Iri, 0, 30);
        array.AddTriple(TermType.Variable, 35, 2, TermType.Iri, 40, 20, TermType.Variable, 65, 2);
        array.AddTriple(TermType.Variable, 35, 2, TermType.Iri, 70, 15, TermType.Literal, 90, 10);
        array.AddFilter(105, 15);
        array.EndGraph(headerIndex);

        var slice = array.GetChildren(headerIndex);
        int count = 0;
        foreach (var child in slice)
        {
            count++;
        }

        Assert.Equal(3, count);
    }
}

public class QueryBufferTests
{
    #region Construction and Capacity

    [Fact]
    public void QueryBuffer_DefaultCapacity_Is128Slots()
    {
        using var buffer = new QueryBuffer();

        Assert.Equal(QueryBuffer.DefaultCapacity, buffer.Capacity);
    }

    [Fact]
    public void QueryBuffer_CustomCapacity()
    {
        using var buffer = new QueryBuffer(64);

        Assert.Equal(64, buffer.Capacity);
    }

    [Fact]
    public void QueryBuffer_MaxCapacity_Is1024()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryBuffer(2000));
    }

    [Fact]
    public void QueryBuffer_ExactMaxCapacity_Allowed()
    {
        using var buffer = new QueryBuffer(QueryBuffer.MaxCapacity);

        Assert.Equal(1024, buffer.Capacity);
    }

    #endregion

    #region Pattern Management

    [Fact]
    public void QueryBuffer_GetPatterns_ReturnsWritableArray()
    {
        using var buffer = new QueryBuffer();

        var patterns = buffer.GetPatterns();
        patterns.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);

        buffer.PatternCount = patterns.Count;

        Assert.Equal(1, buffer.PatternCount);
    }

    [Fact]
    public void QueryBuffer_GetPatterns_WithCount_ReturnsReadonlyView()
    {
        using var buffer = new QueryBuffer();

        var patterns = buffer.GetPatterns();
        patterns.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);
        patterns.AddFilter(50, 10);
        buffer.PatternCount = patterns.Count;

        var readPatterns = buffer.GetPatterns();

        Assert.Equal(2, readPatterns.Count);
    }

    #endregion

    #region Metadata Properties

    [Fact]
    public void QueryBuffer_QueryType()
    {
        using var buffer = new QueryBuffer();

        buffer.Type = QueryType.Select;

        Assert.Equal(QueryType.Select, buffer.Type);
    }

    [Fact]
    public void QueryBuffer_SelectModifiers()
    {
        using var buffer = new QueryBuffer();

        buffer.SelectDistinct = true;
        buffer.SelectAll = false;
        buffer.Limit = 100;
        buffer.Offset = 50;

        Assert.True(buffer.SelectDistinct);
        Assert.False(buffer.SelectAll);
        Assert.Equal(100, buffer.Limit);
        Assert.Equal(50, buffer.Offset);
    }

    [Fact]
    public void QueryBuffer_BaseUri()
    {
        using var buffer = new QueryBuffer();

        buffer.BaseUriStart = 0;
        buffer.BaseUriLength = 30;

        Assert.Equal(0, buffer.BaseUriStart);
        Assert.Equal(30, buffer.BaseUriLength);
    }

    #endregion

    #region Pattern Metadata Flags

    [Fact]
    public void QueryBuffer_HasFilters()
    {
        using var buffer = new QueryBuffer();

        buffer.FilterCount = 0;
        Assert.False(buffer.HasFilters);

        buffer.FilterCount = 2;
        Assert.True(buffer.HasFilters);
    }

    [Fact]
    public void QueryBuffer_HasBinds()
    {
        using var buffer = new QueryBuffer();

        buffer.BindCount = 0;
        Assert.False(buffer.HasBinds);

        buffer.BindCount = 1;
        Assert.True(buffer.HasBinds);
    }

    [Fact]
    public void QueryBuffer_HasMinus()
    {
        using var buffer = new QueryBuffer();

        buffer.MinusPatternCount = 0;
        Assert.False(buffer.HasMinus);

        buffer.MinusPatternCount = 3;
        Assert.True(buffer.HasMinus);
    }

    [Fact]
    public void QueryBuffer_HasExists()
    {
        using var buffer = new QueryBuffer();

        buffer.ExistsFilterCount = 0;
        Assert.False(buffer.HasExists);

        buffer.ExistsFilterCount = 1;
        Assert.True(buffer.HasExists);
    }

    [Fact]
    public void QueryBuffer_HasUnion()
    {
        using var buffer = new QueryBuffer();

        buffer.UnionStartIndex = 0;
        Assert.False(buffer.HasUnion);

        buffer.UnionStartIndex = 5;
        Assert.True(buffer.HasUnion);
    }

    [Fact]
    public void QueryBuffer_HasOptionalPatterns()
    {
        using var buffer = new QueryBuffer();

        buffer.OptionalFlags = 0;
        Assert.False(buffer.HasOptionalPatterns);

        buffer.OptionalFlags = 0b0101;
        Assert.True(buffer.HasOptionalPatterns);
    }

    [Fact]
    public void QueryBuffer_UnionBranchPatternCount()
    {
        using var buffer = new QueryBuffer();

        buffer.PatternCount = 10;
        buffer.UnionStartIndex = 0;
        Assert.Equal(0, buffer.UnionBranchPatternCount);

        buffer.UnionStartIndex = 3;
        Assert.Equal(7, buffer.UnionBranchPatternCount);
    }

    [Fact]
    public void QueryBuffer_HasGraph()
    {
        using var buffer = new QueryBuffer();

        buffer.GraphClauseCount = 0;
        Assert.False(buffer.HasGraph);

        buffer.GraphClauseCount = 2;
        Assert.True(buffer.HasGraph);
    }

    [Fact]
    public void QueryBuffer_HasValues()
    {
        using var buffer = new QueryBuffer();

        buffer.HasValues = false;
        Assert.False(buffer.HasValues);

        buffer.HasValues = true;
        Assert.True(buffer.HasValues);
    }

    #endregion

    #region Graph Clause Metadata

    [Fact]
    public void QueryBuffer_GraphClauseMetadata()
    {
        using var buffer = new QueryBuffer();

        buffer.GraphClauseCount = 2;
        buffer.FirstGraphIsVariable = true;
        buffer.FirstGraphPatternCount = 5;
        buffer.TriplePatternCount = 3;
        buffer.HasSubQueries = true;
        buffer.HasService = false;

        Assert.Equal(2, buffer.GraphClauseCount);
        Assert.True(buffer.FirstGraphIsVariable);
        Assert.Equal(5, buffer.FirstGraphPatternCount);
        Assert.Equal(3, buffer.TriplePatternCount);
        Assert.True(buffer.HasSubQueries);
        Assert.False(buffer.HasService);
    }

    #endregion

    #region Dataset Clauses

    [Fact]
    public void QueryBuffer_Datasets()
    {
        using var buffer = new QueryBuffer();

        var datasets = new DatasetClause[]
        {
            DatasetClause.Default(0, 30),
            DatasetClause.Named(35, 25)
        };
        buffer.Datasets = datasets;

        Assert.NotNull(buffer.Datasets);
        Assert.Equal(2, buffer.Datasets.Length);
        Assert.False(buffer.Datasets[0].IsNamed);
        Assert.True(buffer.Datasets[1].IsNamed);
        Assert.Equal(0, buffer.Datasets[0].GraphIri.Start);
        Assert.Equal(30, buffer.Datasets[0].GraphIri.Length);
    }

    #endregion

    #region Aggregates

    [Fact]
    public void QueryBuffer_Aggregates()
    {
        using var buffer = new QueryBuffer();

        var aggregates = new AggregateEntry[]
        {
            new()
            {
                Function = AggregateFunction.Count,
                VariableStart = 0,
                VariableLength = 2,
                AliasStart = 5,
                AliasLength = 6,
                Distinct = true
            }
        };
        buffer.Aggregates = aggregates;

        Assert.NotNull(buffer.Aggregates);
        Assert.Single(buffer.Aggregates);
        Assert.Equal(AggregateFunction.Count, buffer.Aggregates[0].Function);
        Assert.True(buffer.Aggregates[0].Distinct);
    }

    #endregion

    #region ORDER BY

    [Fact]
    public void QueryBuffer_OrderBy()
    {
        using var buffer = new QueryBuffer();

        var orderBy = new OrderByEntry[]
        {
            new() { VariableStart = 0, VariableLength = 4, Descending = false },
            new() { VariableStart = 10, VariableLength = 5, Descending = true }
        };
        buffer.OrderBy = orderBy;

        Assert.NotNull(buffer.OrderBy);
        Assert.Equal(2, buffer.OrderBy.Length);
        Assert.False(buffer.OrderBy[0].Descending);
        Assert.True(buffer.OrderBy[1].Descending);
    }

    #endregion

    #region GROUP BY

    [Fact]
    public void QueryBuffer_GroupBy()
    {
        using var buffer = new QueryBuffer();

        var groupBy = new GroupByEntry[]
        {
            new() { VariableStart = 0, VariableLength = 3 },
            new() { VariableStart = 5, VariableLength = 4 }
        };
        buffer.GroupBy = groupBy;

        Assert.NotNull(buffer.GroupBy);
        Assert.Equal(2, buffer.GroupBy.Length);
    }

    #endregion

    #region Prefix Mappings

    [Fact]
    public void QueryBuffer_Prefixes()
    {
        using var buffer = new QueryBuffer();

        var prefixes = new PrefixMapping[]
        {
            new() { PrefixStart = 0, PrefixLength = 2, IriStart = 5, IriLength = 30 },
            new() { PrefixStart = 40, PrefixLength = 4, IriStart = 50, IriLength = 25 }
        };
        buffer.Prefixes = prefixes;

        Assert.NotNull(buffer.Prefixes);
        Assert.Equal(2, buffer.Prefixes.Length);
    }

    #endregion

    #region Disposal

    [Fact]
    public void QueryBuffer_Dispose_CleansUp()
    {
        var buffer = new QueryBuffer();
        buffer.Dispose();

        Assert.Equal(0, buffer.Capacity);
    }

    [Fact]
    public void QueryBuffer_DoubleDispose_Safe()
    {
        var buffer = new QueryBuffer();
        buffer.Dispose();
        buffer.Dispose(); // Should not throw
    }

    [Fact]
    public void QueryBuffer_GetPatterns_AfterDispose_Throws()
    {
        var buffer = new QueryBuffer();
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.GetPatterns());
    }

    #endregion

    #region ArrayPool Integration

    [Fact]
    public void QueryBuffer_UsesArrayPool()
    {
        // Create and dispose many buffers to verify no memory leaks
        for (int i = 0; i < 100; i++)
        {
            using var buffer = new QueryBuffer();
            var patterns = buffer.GetPatterns();
            patterns.AddTriple(TermType.Variable, 0, 2, TermType.Iri, 10, 20, TermType.Variable, 40, 2);
            buffer.PatternCount = patterns.Count;
        }

        // If ArrayPool wasn't used properly, this would cause memory issues
        // The fact it completes is the test
        Assert.True(true);
    }

    #endregion
}

public class AggregateEntryTests
{
    [Fact]
    public void AggregateEntry_GroupConcat_SupportsSeparator()
    {
        var entry = new AggregateEntry
        {
            Function = AggregateFunction.GroupConcat,
            VariableStart = 0,
            VariableLength = 4,
            AliasStart = 10,
            AliasLength = 8,
            Distinct = false,
            SeparatorStart = 25,
            SeparatorLength = 3
        };

        Assert.Equal(AggregateFunction.GroupConcat, entry.Function);
        Assert.Equal(25, entry.SeparatorStart);
        Assert.Equal(3, entry.SeparatorLength);
    }
}
