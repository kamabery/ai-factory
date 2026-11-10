using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>An idea on the board.</summary>
public sealed class Idea(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string Id => Json.Text(Node["id"]);
    public string? Status => Json.Str(Node["status"]);
    public string? Target => Json.Str(Node["target"]);
}
