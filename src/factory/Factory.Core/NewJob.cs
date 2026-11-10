using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>A job <see cref="Backlog.AddJob"/> put on the board: its spec, numbered, and its stage event.</summary>
public sealed record NewJob(Job Job, IReadOnlyList<string> Spec, JsonObject Event);
