using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// The run mode's LLM: local, claude logged in to a claude.ai subscription as the runner starts it (its child
/// environment); cloud, a one-token request to each pinned Foundry deployment.
/// </summary>
public sealed class LlmCheck(ILlmProvider llm, IProcessRunner processes, IFoundryProbe foundry) : IDoctorCheck
{
    public string Name => "llm";

    public DoctorResult Check()
    {
        IReadOnlyDictionary<string, string?> env;
        try { env = llm.ChildEnvironment(); }
        catch (FactoryException e) { return DoctorResult.Fail($"cloud mode: {e.Message}", "factory init --foundry <resource>"); }
        return env.GetValueOrDefault("CLAUDE_CODE_USE_FOUNDRY") == "1" ? Cloud(env) : Local(env);
    }

    /// <summary>Whether claude, run with the local child environment, is logged in to a subscription; null when it is.</summary>
    public string? NotSubscribed(IReadOnlyDictionary<string, string?> env)
    {
        ProcessResult r;
        try { r = processes.Run(ClaudeCli.Executable(), ["auth", "status"], "", null, env); }
        catch (System.ComponentModel.Win32Exception) { return "claude is not installed"; }
        JsonNode? status = null;
        try { status = JsonNode.Parse(r.StdOut); } catch (JsonException) { }
        if (status?["loggedIn"]?.GetValue<bool>() != true) return "claude is not logged in";
        var method = PsJsonReader.Str(status["authMethod"]);
        return method == "claude.ai" ? null : $"claude is logged in with {method}, not a claude.ai subscription";
    }

    private DoctorResult Local(IReadOnlyDictionary<string, string?> env) =>
        NotSubscribed(env) is { } why
            ? DoctorResult.Fail($"local mode: {why}", "run claude, then /login, and choose your claude.ai account")
            : DoctorResult.Ok("local mode: claude is logged in to a claude.ai subscription");

    private DoctorResult Cloud(IReadOnlyDictionary<string, string?> env)
    {
        var resource = env["ANTHROPIC_FOUNDRY_RESOURCE"]!;
        var deployments = LlmProvider.FoundryModels.Select(m => env.GetValueOrDefault(m.Name)).OfType<string>().Distinct().ToList();
        var failed = deployments.Select(d => (d, Why: foundry.Ping(resource, d))).Where(p => p.Why is not null).ToList();
        if (failed.Count > 0)
            return DoctorResult.Fail($"cloud mode, {resource}: {string.Join("; ", failed.Select(f => $"{f.d}: {f.Why}"))}",
                "az login; check Foundry User on the resource and each deployment (docs/FOUNDRY.md)");
        return DoctorResult.Ok($"cloud mode, {resource}: {string.Join(", ", deployments)} answered");
    }
}
