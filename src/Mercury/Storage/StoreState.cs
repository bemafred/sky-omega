using System.IO;
using System.Text;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// Index construction state for a QuadStore. Persisted as a simple text file
/// in the store directory so the query planner knows which indexes are available.
/// </summary>
public enum StoreIndexState
{
    /// <summary>All indexes built and consistent. Full query optimization available.</summary>
    Ready,

    /// <summary>GSPO populated, secondary indexes empty. GSPO-only query plans.</summary>
    PrimaryOnly,

    /// <summary>GPOS index being built from GSPO scan.</summary>
    BuildingGPOS,

    /// <summary>GOSP index being built from GSPO scan.</summary>
    BuildingGOSP,

    /// <summary>TGSP index being built from GSPO scan.</summary>
    BuildingTGSP,

    /// <summary>Trigram index being rebuilt from object literals.</summary>
    BuildingTrigram
}

/// <summary>
/// Reads and writes store index state to a file in the store directory.
/// </summary>
internal static class StoreStateFile
{
    private const string FileName = "index-state";

    public static StoreIndexState Read(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, FileName);
        if (!File.Exists(path))
            return StoreIndexState.Ready; // No file = legacy store, all indexes assumed built

        var text = File.ReadAllText(path).Trim();
        return text switch
        {
            "Ready" => StoreIndexState.Ready,
            "PrimaryOnly" => StoreIndexState.PrimaryOnly,
            "BuildingGPOS" => StoreIndexState.BuildingGPOS,
            "BuildingGOSP" => StoreIndexState.BuildingGOSP,
            "BuildingTGSP" => StoreIndexState.BuildingTGSP,
            "BuildingTrigram" => StoreIndexState.BuildingTrigram,
            _ => StoreIndexState.Ready // Unknown state = assume ready
        };
    }

    public static void Write(string baseDirectory, StoreIndexState state)
    {
        var path = Path.Combine(baseDirectory, FileName);
        File.WriteAllText(path, state.ToString());
    }
}
