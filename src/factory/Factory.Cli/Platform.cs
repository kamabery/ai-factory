using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary>
/// <see cref="IPlatform"/> through <c>az postgres flexible-server</c> (or FACTORY_AZ_CMD), under the human's az login or,
/// in a deployed factory, the managed identity's. az where it fits better than HCP Terraform, as for Postgres roles.
/// </summary>
public sealed class Platform(IDotEnv dotEnv, IToolRunner tools, IPause pause, IOptions<DeployOptions> options) : IPlatform
{
    public bool Configured => dotEnv.Get("HCP_ORG") is not null && !string.IsNullOrWhiteSpace(dotEnv.Get("FACTORY_PLATFORM_SERVER_ID"));

    public string Status() => Word(State());

    public string Start()
    {
        var state = State();
        while (state is "Stopping")
        {
            pause.Sleep(TimeSpan.FromSeconds(options.Value.Poll));
            state = State();
        }
        while (state is "Starting")
        {
            pause.Sleep(TimeSpan.FromSeconds(options.Value.Poll));
            state = State();
        }
        // az start is a long-running operation it waits on, so it returns once the server is Ready.
        if (state is not "Ready") Az("start");
        return "platform: awake";
    }

    public string Sleep()
    {
        Az("stop", "--no-wait");
        return "platform: stopping";
    }

    private static string Word(string state) => state switch
    {
        "Ready" => "awake",
        "Stopped" => "asleep",
        _ => state.ToLowerInvariant(),
    };

    private string State() => Az("show", "--query", "state", "--output", "tsv").Trim();

    private string Az(string verb, params string[] more)
    {
        if (dotEnv.Get("HCP_ORG") is null) throw new FactoryException("platform refused: deploys are off");
        var id = dotEnv.Get("FACTORY_PLATFORM_SERVER_ID");
        if (string.IsNullOrWhiteSpace(id))
            throw new FactoryException("platform refused: no FACTORY_PLATFORM_SERVER_ID in .env; run factory previews-env");
        var r = tools.Az(["postgres", "flexible-server", verb, "--ids", id, .. more]);
        if (r.Code != 0) throw new FactoryException($"platform refused: az {verb} failed: {FirstLine(r.Out)}");
        return r.Out;
    }

    private static string FirstLine(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault() ?? "";
    }
}
