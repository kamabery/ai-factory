using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>An idea on the board.</summary>
public sealed class Idea(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string Id => PsJsonReader.Text(Node["id"]);
    public string? Status => PsJsonReader.Str(Node["status"]);
    public string? Target => PsJsonReader.Str(Node["target"]);
}
