using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// <c>factory runner</c> detached, or with FACTORY_RUNNER_JOB, one execution of that Container Apps job through
/// <c>az rest</c> (FACTORY_AZ_CMD in the tests), as the identity az is signed in as.
/// </summary>
public sealed class RunnerStarter(IOptions<CloudOptions> cloud, IOptions<StateOptions> state, IFactoryLauncher launcher, IProcessRunner processes) : IRunnerStarter
{
    private const string Api = "api-version=2025-01-01";

    public bool StartsExecutions => !string.IsNullOrEmpty(cloud.Value.RunnerJob);

    public string Start()
    {
        if (!StartsExecutions)
        {
            launcher.StartDetached(["runner"]);
            return "started runner";
        }
        string[] args = ["rest", "--method", "post", "--url", $"https://management.azure.com{cloud.Value.RunnerJob}/start?{Api}"];
        var az = state.Value.AzCmd;
        var r = string.IsNullOrEmpty(az)
            ? processes.Run(StateToken.AzProgram, args, "")
            : processes.Run(CommandOverride.Parse("FACTORY_AZ_CMD", az)[0], [.. CommandOverride.Parse("FACTORY_AZ_CMD", az).Skip(1), .. args], "");
        if (r.ExitCode != 0) throw new FactoryException($"runner job start failed: {(r.StdErr + r.StdOut).Trim()}");
        string? name = null;
        try { name = (string?)JsonNode.Parse(r.StdOut)?["name"]; }
        catch (JsonException) { }
        return $"started runner execution {name ?? "(unnamed)"}";
    }
}
