using System.Text.Json.Nodes;

namespace Factory.Runner.Abstractions;

/// <summary>A claude pass's exit code and its result: the last output line carrying <c>total_cost_usd</c>, parsed, or null.</summary>
public sealed record ClaudeRun(int Code, JsonObject? Result);
