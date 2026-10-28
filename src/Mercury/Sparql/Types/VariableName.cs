using System;
using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Sparql;

/// <summary>
/// A SPARQL variable's identity. [108] Var ::= VAR1 | VAR2: <c>?x</c> and <c>$x</c> are one variable (SPARQL 1.1
/// §19.8), so a variable is its name without the sigil. Every test for a variable's start, and every hash or
/// comparison of a variable's name, goes through here.
/// </summary>
internal static class VariableName
{
    /// <summary>True for the characters that start a variable: <c>?</c> and <c>$</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsSigil(char ch) => ch == '?' || ch == '$';

    /// <summary>The name without its sigil; a name written without one is returned unchanged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<char> WithoutSigil(ReadOnlySpan<char> name)
        => name.Length > 0 && IsSigil(name[0]) ? name[1..] : name;

    /// <summary>The name without its sigil; a name written without one is returned unchanged.</summary>
    public static string WithoutSigil(string name)
        => name.Length > 0 && IsSigil(name[0]) ? name[1..] : name;

    /// <summary>The variable's identity hash: <c>?x</c>, <c>$x</c> and <c>x</c> hash alike.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash(ReadOnlySpan<char> name) => Fnv1a.Hash(WithoutSigil(name));

    /// <summary>True when two names are the same variable, whatever their sigils.</summary>
    public static bool Same(ReadOnlySpan<char> a, ReadOnlySpan<char> b) => WithoutSigil(a).SequenceEqual(WithoutSigil(b));
}
