using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary><c>deploy</c>: the job's preview.</summary>
public sealed class Deploy(JsonObject node)
{
    public JsonObject Node { get; } = node;
    public string? Status => PsJsonReader.Str(Node["status"]);
    public int Attempts => PsJsonReader.Int(Node["attempts"]);
    public string? DestroyStartedAt => PsJsonReader.Str(Node["destroyStartedAt"]);
    public int DestroyFailures => PsJsonReader.Int(Node["destroyFailures"]);
}
