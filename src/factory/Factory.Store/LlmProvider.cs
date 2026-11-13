using Factory.Core;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// llm.ps1's Get-ProviderEnv. The mode is FACTORY_MODE from the environment, else .env, else <c>Factory:Mode</c>
/// (<see cref="LlmOptions.ConfiguredMode"/>); local is the subscription, cloud is Claude in Microsoft Foundry, keyless.
/// Jobs (the runner) and idea turns (the CLI) both run their claude child on it.
/// </summary>
public sealed class LlmProvider(IOptions<LlmOptions> options, IDotEnv dotEnv) : ILlmProvider
{
    /// <summary>Everything that would route claude anywhere but the mode's own provider when inherited.</summary>
    public static readonly string[] ProviderVars =
    [
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_USE_FOUNDRY",
        "ANTHROPIC_FOUNDRY_RESOURCE", "ANTHROPIC_FOUNDRY_BASE_URL", "ANTHROPIC_FOUNDRY_API_KEY", "ANTHROPIC_FOUNDRY_AUTH_TOKEN",
    ];

    /// <summary>The Foundry deployments claude's model aliases map to, unless .env pins another.</summary>
    public static readonly (string Name, string Default)[] FoundryModels =
    [
        ("ANTHROPIC_DEFAULT_OPUS_MODEL", "claude-opus-5-5"),
        ("ANTHROPIC_DEFAULT_SONNET_MODEL", "claude-sonnet-5-5"),
        ("ANTHROPIC_DEFAULT_HAIKU_MODEL", "claude-haiku-5-5"),
    ];

    /// <summary>The run mode as configured, before validation; null when nothing sets it.</summary>
    public string? Mode => Setting("FACTORY_MODE") ?? (string.IsNullOrEmpty(options.Value.ConfiguredMode) ? null : options.Value.ConfiguredMode);

    public IReadOnlyDictionary<string, string?> ChildEnvironment()
    {
        var env = ProviderVars.ToDictionary(n => n, _ => (string?)null);
        if (RunMode.Provider(Mode) == "subscription") return env;
        var resource = Setting("FACTORY_FOUNDRY_RESOURCE") ?? throw new FactoryException("no FACTORY_FOUNDRY_RESOURCE");
        env["CLAUDE_CODE_USE_FOUNDRY"] = "1";
        env["ANTHROPIC_FOUNDRY_RESOURCE"] = resource;
        foreach (var (name, fallback) in FoundryModels) env[name] = Setting(name) ?? fallback;
        return env;
    }

    /// <summary>llm.ps1's Get-LlmSetting: the process environment first (a deployed factory sets it there), then .env.</summary>
    private string? Setting(string name) =>
        options.Value.Environment.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v) ? v : dotEnv.Get(name);
}
