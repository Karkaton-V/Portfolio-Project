using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAtlas.Core;

/// <summary>Reads and writes <see cref="DependencyGraph"/> as camelCase JSON with enums as strings.</summary>
public static class GraphJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(DependencyGraph graph) => JsonSerializer.Serialize(graph, Options);

    public static DependencyGraph Deserialize(string json) =>
        JsonSerializer.Deserialize<DependencyGraph>(json, Options)
        ?? throw new JsonException("The JSON document does not contain a dependency graph.");
}
