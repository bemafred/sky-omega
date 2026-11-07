using System.Text.Encodings.Web;
using System.Text.Json;

namespace SkyOmega.DrHook.Engine;

/// <summary>
/// How DrHook writes every result its operator reads: the engine's diagnostics (<c>ToJson</c>) and every MCP tool's
/// response. The reader is the LLM operator, over an MCP text channel, never HTML.
/// </summary>
/// <remarks>
/// <para>Relaxed escaping: frame names, type names and paths are full of + &lt; &gt; &amp; ' and ", which the
/// default encoder writes as +, < and the like, and the operator has to decode them in its head. The
/// relaxed encoder still escapes what JSON itself requires (quotes, backslash, control characters).</para>
/// <para>No type resolver: results are <c>JsonNode</c> trees, which write themselves. Reflection-based
/// serialization is disabled in trimmed, AOT and file-based hosts, and the first write makes these options
/// read-only.</para>
/// </remarks>
public static class OperatorJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
