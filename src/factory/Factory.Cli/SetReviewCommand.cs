using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>set-review &lt;id&gt; &lt;role&gt; &lt;state&gt;</c>: one reviewer's state, as the orchestrator records it.</summary>
public sealed class SetReviewCommand(TimeProvider clock) : ICommand
{
    private static readonly string[] States = ["pending", "running", "PASS", "CHANGES"];

    public string Name => "set-review";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        var role = args.Arg(2);
        var state = args.Arg(3);
        if (!Stages.Roles.Contains(role, StringComparer.OrdinalIgnoreCase)) throw new FactoryException($"unknown role {role}");
        if (!States.Contains(state, StringComparer.OrdinalIgnoreCase)) throw new FactoryException($"unknown review state {state}");
        var reviews = j.Node["reviews"] as JsonObject ?? throw new FactoryException($"{j.Id} has no reviews");
        // A PowerShell hashtable's keys ignore case: set-review J-001 Code PASS sets "code".
        var key = reviews.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, role, StringComparison.OrdinalIgnoreCase)) ?? role;
        reviews[key] = state;
        j.Node["updatedAt"] = Iso.Format(clock.GetUtcNow());
        session.Save();
        return CommandResult.Ok();
    }
}
