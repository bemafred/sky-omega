using System.Runtime.CompilerServices;

namespace SkyOmega.Mercury.Sparql;

internal static class Fnv1a
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash(ReadOnlySpan<char> value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in value)
            {
                hash ^= ch;
                hash *= 16777619;
            }
            return (int)hash;
        }
    }
}
