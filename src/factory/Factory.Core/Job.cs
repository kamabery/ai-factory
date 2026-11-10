using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>A job on the board: a typed view over its JSON object, which keeps the file's keys and order.</summary>
public sealed class Job(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string Id => Json.Text(Node["id"]);
    public string? Idea => Json.Str(Node["idea"]);
    public string Stage => Json.Text(Node["stage"]);
    public int Round => Json.Int(Node["round"]);
    public string? Kind => Json.Str(Node["kind"]);
    public bool IsBootstrap => Json.Is(Kind, "bootstrap");
    public string? Target => Json.Str(Node["target"]);
    public string? Base => Json.Str(Node["base"]);
    public string? ApprovedAt => Json.Str(Node["approvedAt"]);
    public string? Resume => Json.Str(Node["resume"]);
    public Flag? Flag => Node["flag"] is JsonObject f ? new Flag(f) : null;
    public Deploy? Deploy => Node["deploy"] is JsonObject d ? new Deploy(d) : null;
}
