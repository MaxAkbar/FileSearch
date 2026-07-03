using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileSearch.Mcp;

/// <summary>
/// One serializer shape for every tool result: camelCase, nulls dropped,
/// compact (results are consumed by models, where indentation is only token
/// overhead). Property names line up with the CLI's one-shot JSON contract.
/// </summary>
internal static class McpJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
