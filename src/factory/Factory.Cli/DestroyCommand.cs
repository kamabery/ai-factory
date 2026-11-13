using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>destroy &lt;id&gt;</c> (contract, "Deploy"): the job's preview down, resources then workspace, recorded on the job; it never
/// moves the job. A failure puts the status back, counts it and keeps its error, for <c>sweep</c> to retry: HCP's
/// auto-destroy fires once, so it is no backstop for a destroy that errors.
/// </summary>
public sealed class DestroyCommand(IDeployService deploy, TimeProvider clock) : ICommand
{
    public string Name => "destroy";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var id = args.Arg(1);
        var j = session.Board.Job(id);
        if (j.Node["deploy"] is not JsonObject d || d["status"] is null || PsJsonReader.Is(PsJsonReader.Str(d["status"]), "down"))
            return CommandResult.Ok($"no preview for {j.Id}");
        // A destroy that died left 'destroying'; what it had reached is unknown, so not 'up'.
        var before = PsJsonReader.Is(PsJsonReader.Str(d["status"]), "destroying") ? "failed" : PsJsonReader.Str(d["status"]);
        d["status"] = "destroying";
        d["destroyStartedAt"] = Iso.Format(clock.GetUtcNow());
        session.Save();

        var err = deploy.Destroy(session, j);
        j = session.Board.Job(id);
        d = j.Node["deploy"]!.AsObject();
        if (!string.IsNullOrEmpty(err))
        {
            d["status"] = before;
            d["destroyError"] = err;
            d["destroyFailures"] = PsJsonReader.Int(d["destroyFailures"]) + 1;
            session.Save();
            session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "destroy"), ("detail", $"failed: {err}")));
            throw new FactoryException($"destroy failed: {err}");
        }
        d["status"] = "down";
        d["urls"] = null;
        d["destroyError"] = null;
        d["destroyFailures"] = 0;
        session.Save();
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "destroy"), ("detail", "down")));
        return CommandResult.Ok("down");
    }
}
