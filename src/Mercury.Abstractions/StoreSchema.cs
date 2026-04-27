using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SkyOmega.Mercury.Abstractions;

/// <summary>
/// Durable schema for a Mercury store. Written to <c>store-schema.json</c> in the store's
/// base directory at creation time. Every open validates the runtime's requested profile
/// against the stored schema — mismatch is a hard error (ADR-029 Decision 2).
/// </summary>
/// <remarks>
/// The <see cref="KeyLayoutVersion"/> field discriminates incompatible schema evolutions.
/// A build that sees a higher version than it knows must refuse to open the store.
/// Unknown JSON fields are tolerated (forward-compat); version mismatches are not.
/// </remarks>
public sealed record StoreSchema(
    StoreProfile Profile,
    IReadOnlyList<string> Indexes,
    bool HasGraph,
    bool HasTemporal,
    bool HasVersioning,
    int KeyLayoutVersion,
    AtomStoreImplementation AtomStore = AtomStoreImplementation.Hash)
{
    /// <summary>Canonical file name for the schema record in a store directory.</summary>
    public const string FileName = "store-schema.json";

    /// <summary>Current schema layout version emitted by this Mercury build.</summary>
    public const int CurrentKeyLayoutVersion = 1;

    /// <summary>
    /// Canonical schema for a profile. Indexes and capability flags are derived from the
    /// profile per the ADR-029 matrix; callers never hand-assemble a <see cref="StoreSchema"/>
    /// for a known profile.
    /// </summary>
    public static StoreSchema ForProfile(StoreProfile profile) => profile switch
    {
        StoreProfile.Cognitive => new StoreSchema(
            Profile: StoreProfile.Cognitive,
            Indexes: new[] { "gspo", "gpos", "gosp", "tgsp" },
            HasGraph: true,
            HasTemporal: true,
            HasVersioning: true,
            KeyLayoutVersion: CurrentKeyLayoutVersion),

        StoreProfile.Graph => new StoreSchema(
            Profile: StoreProfile.Graph,
            Indexes: new[] { "gspo", "gpos", "gosp", "tgsp" },
            HasGraph: true,
            HasTemporal: false,
            HasVersioning: true,
            KeyLayoutVersion: CurrentKeyLayoutVersion),

        StoreProfile.Reference => new StoreSchema(
            Profile: StoreProfile.Reference,
            Indexes: new[] { "gspo", "gpos" },
            HasGraph: true,
            HasTemporal: false,
            HasVersioning: false,
            KeyLayoutVersion: CurrentKeyLayoutVersion),

        StoreProfile.Minimal => new StoreSchema(
            Profile: StoreProfile.Minimal,
            Indexes: new[] { "gspo" },
            HasGraph: false,
            HasTemporal: false,
            HasVersioning: false,
            KeyLayoutVersion: CurrentKeyLayoutVersion),

        _ => throw new System.ArgumentOutOfRangeException(nameof(profile), profile, "Unknown StoreProfile")
    };

    /// <summary>
    /// Serialize to canonical JSON. Fields are emitted in a stable order so two stores with
    /// the same schema produce byte-identical files.
    /// </summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions { Indented = true };
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            writer.WriteStartObject();
            writer.WriteString("profile", Profile.ToString());
            writer.WritePropertyName("indexes");
            writer.WriteStartArray();
            foreach (var idx in Indexes)
                writer.WriteStringValue(idx);
            writer.WriteEndArray();
            writer.WriteBoolean("hasGraph", HasGraph);
            writer.WriteBoolean("hasTemporal", HasTemporal);
            writer.WriteBoolean("hasVersioning", HasVersioning);
            writer.WriteNumber("keyLayoutVersion", KeyLayoutVersion);
            writer.WriteString("atomStore", AtomStore.ToString());
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Parse JSON. Throws <see cref="InvalidStoreSchemaException"/> on malformed input,
    /// unknown profile, or layout-version mismatch.
    /// </summary>
    public static StoreSchema FromJson(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidStoreSchemaException("store-schema.json is not valid JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidStoreSchemaException("store-schema.json root is not a JSON object.");

            var profileText = GetRequiredString(root, "profile");
            if (!System.Enum.TryParse<StoreProfile>(profileText, ignoreCase: false, out var profile))
                throw new InvalidStoreSchemaException(
                    $"store-schema.json has unknown profile \"{profileText}\". This Mercury build knows: " +
                    string.Join(", ", System.Enum.GetNames<StoreProfile>()) + ".");

            var indexes = GetRequiredStringArray(root, "indexes");
            var hasGraph = GetRequiredBool(root, "hasGraph");
            var hasTemporal = GetRequiredBool(root, "hasTemporal");
            var hasVersioning = GetRequiredBool(root, "hasVersioning");
            var keyLayoutVersion = GetRequiredInt(root, "keyLayoutVersion");

            if (keyLayoutVersion > CurrentKeyLayoutVersion)
                throw new InvalidStoreSchemaException(
                    $"store-schema.json has keyLayoutVersion {keyLayoutVersion}, but this Mercury build " +
                    $"only supports versions up to {CurrentKeyLayoutVersion}. Upgrade Mercury or reload the store.");

            // ADR-034: atomStore is optional for backward compat with stores written before
            // the field was introduced. Missing field defaults to Hash; existing stores
            // (wiki-21b-ref etc.) keep their HashAtomStore behavior unchanged.
            var atomStore = AtomStoreImplementation.Hash;
            if (root.TryGetProperty("atomStore", out var atomStoreProp) && atomStoreProp.ValueKind == JsonValueKind.String)
            {
                var asText = atomStoreProp.GetString();
                if (!System.Enum.TryParse<AtomStoreImplementation>(asText, ignoreCase: false, out atomStore))
                    throw new InvalidStoreSchemaException(
                        $"store-schema.json has unknown atomStore \"{asText}\". This Mercury build knows: " +
                        string.Join(", ", System.Enum.GetNames<AtomStoreImplementation>()) + ".");
            }

            return new StoreSchema(profile, indexes, hasGraph, hasTemporal, hasVersioning, keyLayoutVersion, atomStore);
        }
    }

    /// <summary>
    /// Read from <c>store-schema.json</c> in a store directory. Returns null if the file
    /// does not exist (legacy store — caller decides whether to default or refuse).
    /// </summary>
    public static StoreSchema? ReadFrom(string storeBaseDirectory)
    {
        var path = Path.Combine(storeBaseDirectory, FileName);
        if (!File.Exists(path))
            return null;
        return FromJson(File.ReadAllText(path));
    }

    /// <summary>Write this schema to <c>store-schema.json</c> in the given directory.</summary>
    public void WriteTo(string storeBaseDirectory)
    {
        var path = Path.Combine(storeBaseDirectory, FileName);
        File.WriteAllText(path, ToJson());
    }

    private static string GetRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            throw new InvalidStoreSchemaException($"store-schema.json missing or non-string field \"{name}\".");
        return prop.GetString()!;
    }

    private static bool GetRequiredBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) ||
            (prop.ValueKind != JsonValueKind.True && prop.ValueKind != JsonValueKind.False))
            throw new InvalidStoreSchemaException($"store-schema.json missing or non-boolean field \"{name}\".");
        return prop.GetBoolean();
    }

    private static int GetRequiredInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number ||
            !prop.TryGetInt32(out var value))
            throw new InvalidStoreSchemaException($"store-schema.json missing or non-integer field \"{name}\".");
        return value;
    }

    private static IReadOnlyList<string> GetRequiredStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            throw new InvalidStoreSchemaException($"store-schema.json missing or non-array field \"{name}\".");
        var list = new List<string>(prop.GetArrayLength());
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidStoreSchemaException($"store-schema.json \"{name}\" contains a non-string element.");
            list.Add(item.GetString()!);
        }
        return list;
    }
}

/// <summary>Thrown when <c>store-schema.json</c> is malformed, unknown, or incompatible.</summary>
public sealed class InvalidStoreSchemaException : System.Exception
{
    public InvalidStoreSchemaException(string message) : base(message) { }
    public InvalidStoreSchemaException(string message, System.Exception inner) : base(message, inner) { }
}
