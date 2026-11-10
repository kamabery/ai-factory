using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary><c>deploy</c>: the job's preview.</summary>
public sealed class Deploy(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string? Status => Json.Str(Node["status"]);
    public int Attempts => Json.Int(Node["attempts"]);
    public string? DestroyStartedAt => Json.Str(Node["destroyStartedAt"]);
    public int DestroyFailures => Json.Int(Node["destroyFailures"]);
}
