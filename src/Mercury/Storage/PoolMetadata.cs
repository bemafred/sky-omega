// PoolMetadata.cs
// JSON-serializable metadata for QuadStorePool persistence.
// No external dependencies, only BCL (System.Text.Json).
// .NET 10 / C# 14

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkyOmega.Mercury.Storage;

/// <summary>
/// JSON-serializable metadata for a QuadStorePool, persisted to pool.json.
/// </summary>
internal sealed class PoolMetadata
{
    /// <summary>
    /// Metadata format version. Increment when making breaking changes.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>
    /// Name of the currently active store (null if not set).
    /// </summary>
    [JsonPropertyName("active")]
    public string? Active { get; set; }

    /// <summary>
    /// Mapping from logical store names to physical directory GUIDs.
    /// Example: { "primary": "0194a3f8c2e1", "secondary": "0194a3f9b7d4" }
    /// </summary>
    [JsonPropertyName("stores")]
    public Dictionary<string, string> Stores { get; init; } = new();

    /// <summary>
    /// Pool configuration settings.
    /// </summary>
    [JsonPropertyName("settings")]
    public PoolSettings Settings { get; init; } = new();

    /// <summary>
    /// JSON serialization options: camelCase, indented for readability.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Loads metadata from the specified pool.json file.
    /// Returns a new default instance if the file doesn't exist.
    /// </summary>
    /// <param name="poolJsonPath">Full path to pool.json file.</param>
    /// <returns>Loaded or default metadata.</returns>
    /// <exception cref="JsonException">Thrown if the file exists but is malformed.</exception>
    internal static PoolMetadata Load(string poolJsonPath)
    {
        if (!File.Exists(poolJsonPath))
            return new PoolMetadata();

        var json = File.ReadAllText(poolJsonPath);
        return JsonSerializer.Deserialize<PoolMetadata>(json, JsonOptions) ?? new PoolMetadata();
    }

    /// <summary>
    /// Saves metadata to the specified pool.json file using atomic write-rename pattern.
    /// </summary>
    /// <param name="poolJsonPath">Full path to pool.json file.</param>
    /// <remarks>
    /// Uses write-to-temp-then-rename to ensure crash-safety.
    /// If the process crashes during write, the old pool.json remains intact.
    /// </remarks>
    internal void Save(string poolJsonPath)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var tempPath = poolJsonPath + ".tmp";

        // Write to temp file first
        File.WriteAllText(tempPath, json);

        // Atomic rename (overwrites existing)
        File.Move(tempPath, poolJsonPath, overwrite: true);
    }
}

/// <summary>
/// Pool configuration settings, persisted in pool.json.
/// </summary>
internal sealed class PoolSettings
{
    /// <summary>
    /// Maximum disk space in bytes for all stores combined.
    /// 0 means no explicit limit (uses disk budget calculation).
    /// </summary>
    [JsonPropertyName("maxDiskBytes")]
    public long MaxDiskBytes { get; init; }

    /// <summary>
    /// Maximum number of anonymous pooled stores (for Rent()/Return() API).
    /// </summary>
    [JsonPropertyName("maxPooledStores")]
    public int MaxPooledStores { get; init; } = 8;
}
