using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Server;

/// <summary>serve.ps1's Read-Board: board.json as it is on disk, or an empty board.</summary>
public sealed class BoardFile(IFactoryPaths paths, IFileStore files)
{
    public IEnumerable<JsonObject> Jobs() => Items("jobs");

    public IEnumerable<JsonObject> Ideas() => Items("ideas");

    public JsonObject? Job(string id) => Jobs().FirstOrDefault(j => Str(j, "id") == id);

    public static string? Str(JsonNode? node, string name) =>
        node?[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : node?[name]?.ToString();

    private IEnumerable<JsonObject> Items(string name)
    {
        var text = files.ReadText(paths.BoardPath);
        if (string.IsNullOrWhiteSpace(text)) return [];
        return (JsonNode.Parse(text)?[name] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
    }
}
