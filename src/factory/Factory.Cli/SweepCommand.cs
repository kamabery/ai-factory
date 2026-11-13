using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary>
/// <c>sweep</c> (contract, "Deploy"): a detached destroy for each finished job's preview that is not down, and a detached
/// canary-destroy for a canary teardown that failed; prints what it started. Nothing with deploys off, and nothing for a
/// job still in progress: its next round applies over the preview, and a destroy there is the J-022 race.
/// </summary>
public sealed class SweepCommand(IDotEnv dotEnv, IFactoryPaths paths, IFileStore files, IFactoryLauncher launcher, TimeProvider clock,
    IOptions<DeployOptions> options) : ICommand
{
    public string Name => "sweep";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        if (dotEnv.Get("HCP_ORG") is null) return CommandResult.Ok();
        var now = clock.GetUtcNow();
        var stale = options.Value.DestroyStale;
        var started = new List<string>();
        foreach (var j in session.Board.Jobs().Where(j => PsJsonReader.In(j.Stage, Stages.Done, Stages.Discarded) && j.Deploy is not null))
        {
            var d = j.Deploy!;
            if (!Sweep.Due(d.Status, d.DestroyStartedAt, d.DestroyFailures, now, stale)) continue;
            started.Add(j.Id);
            launcher.StartDetached(["destroy", j.Id]);
        }
        if (files.ReadText(Path.Combine(paths.Home, "canary.json")) is { } text && JsonNode.Parse(text)?["teardown"] is JsonObject t
            && Sweep.Due(PsJsonReader.Str(t["status"]), PsJsonReader.Str(t["startedAt"]), PsJsonReader.Int(t["failures"]), now, stale))
        {
            started.Add("canary");
            launcher.StartDetached(["canary-destroy"]);
        }
        return new CommandResult(0, started, []);
    }
}
