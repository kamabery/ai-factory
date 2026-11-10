using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary><c>flag</c>: why a ready or blocked job stopped.</summary>
public sealed class Flag(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string Reason => Json.Text(Node["reason"]);
    public string? Detail => Json.Str(Node["detail"]);
    public string? Why => Json.Str(Node["why"]);
    /// <summary>usage-limit only: the Building stage claude stopped in.</summary>
    public string? Stage => Json.Str(Node["stage"]);
    /// <summary>usage-limit only: claude's session id.</summary>
    public string? Session => Json.Str(Node["session"]);
}
