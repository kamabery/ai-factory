using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Server.Abstractions;
using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.Server;

/// <summary>serve.ps1's Get-EnvValue, Get-Mode and Get-IdeaBlocker; the .env is read on every call, so an edit shows at once.</summary>
public sealed class ServerSettings(IOptions<ServerOptions> options, IFileStore files) : IServerSettings
{
    private ServerOptions O => options.Value;

    public string? Env(string name)
    {
        return EnvFile.Parse(files.ReadText(O.EnvFile)).TryGetValue(name, out var v) && v.Length > 0 ? v : null;
    }

    /// <summary>llm.ps1's Get-LlmSetting: the process environment first, then .env.</summary>
    private string? Llm(string name) => O.Environment.GetValueOrDefault(name) is { Length: > 0 } v ? v : Env(name);

    public string? Mode
    {
        get
        {
            var m = Llm("FACTORY_MODE");
            if (m is null) return "local";
            m = m.Trim().ToLowerInvariant();
            try { RunMode.Provider(m); return m; }
            catch (FactoryException) { return null; }
        }
    }

    public string? IdeaBlocker => Mode switch
    {
        "local" => Env("ANTHROPIC_API_KEY") is null ? $"no ANTHROPIC_API_KEY in {O.EnvFile}" : null,
        "cloud" => Llm("FACTORY_FOUNDRY_RESOURCE") is null ? "no FACTORY_FOUNDRY_RESOURCE (cloud mode)" : null,
        _ => "FACTORY_MODE must be local or cloud",
    };

    public string IdeaModel => Env("FACTORY_IDEA_MODEL") ?? "claude-opus-5-5";
}
