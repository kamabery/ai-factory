using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>A job on the board: a typed view over its JSON object, which keeps the file's keys and order.</summary>
public sealed class Job(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string Id => PsJsonReader.Text(Node["id"]);
    public string? Idea => PsJsonReader.Str(Node["idea"]);
    public string Stage => PsJsonReader.Text(Node["stage"]);
    public int Round => PsJsonReader.Int(Node["round"]);
    public string? Kind => PsJsonReader.Str(Node["kind"]);
    public bool IsBootstrap => PsJsonReader.Is(Kind, "bootstrap");
    public string? Target => PsJsonReader.Str(Node["target"]);
    public string? Base => PsJsonReader.Str(Node["base"]);
    public string? ApprovedAt => PsJsonReader.Str(Node["approvedAt"]);
    public string? Resume => PsJsonReader.Str(Node["resume"]);
    public Flag? Flag => Node["flag"] is JsonObject f ? new Flag(f) : null;
    public Deploy? Deploy => Node["deploy"] is JsonObject d ? new Deploy(d) : null;
}
