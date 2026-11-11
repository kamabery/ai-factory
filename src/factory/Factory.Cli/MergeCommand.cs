using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>merge &lt;id&gt;</c>: the human's drag to Done (contract, "Gate and merge"). A PR merged on GitHub already is recorded;
/// otherwise a clean tree with commits beyond main and a green gate at its head is published and merged on GitHub at the
/// gated sha. Anything in the way leaves the job in ready, flagged.
/// </summary>
public sealed class MergeCommand(IFactoryPaths paths, IGitClient git, IGitHub github, IJobWork work, TimeProvider clock) : ICommand
{
    public string Name => "merge";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var id = args.Arg(1);
        var j = session.Board.Job(id);
        Transitions.RequireStage(j, [Stages.Ready], "merge");
        // The human may have merged the PR on GitHub already: that merge is the acceptance.
        if (work.GitHubMerge(j) is { } onGitHub) return CommandResult.Ok([.. work.CompleteGitHubMerge(session, j, onGitHub)]);
        MergeRules.RequireBaseMerged(session.Board, j);
        if (work.EnsureClone(session.Board, j.Id) is { } err) throw Blocked(session, j, "human", "merge-blocked", err, $"merge blocked: {err}");
        var wt = paths.WorkDir(j.Id);
        if (work.Fetch(wt) is { Code: not 0 } f) throw Blocked(session, j, "human", "merge-blocked", $"fetch failed: {f.Out}", "merge blocked: fetch failed");
        // By content, not status: an EOL-only rewrite (autocrlf + a generator) is not a change, and untracked gate output
        // is not work. safecrlf=false: the CRLF warning is not a path.
        var dirty = git.Run(wt, "-c", "core.quotepath=false", "-c", "core.safecrlf=false", "diff", "--name-only", "HEAD").Out;
        var ahead = dirty.Length > 0 ? "" : git.Run(wt, "rev-list", "--count", $"origin/main..factory/{j.Id}").Out;
        if (MergeRules.WorkBlock(j.Id, dirty, ahead) is { } wb) throw Blocked(session, j, "human", "merge-blocked", wb.Detail, wb.Refusal);
        if (!work.GateGreen(j))
        {
            var g = work.Gate(session, j.Id);
            j = session.Board.Job(id);
            if (g is not null) throw Blocked(session, j, "gate", "merge-blocked", g, $"merge blocked: {g}");
            if (!work.GateGreen(j)) throw Blocked(session, j, "gate", "gate-failed", "gate red at merge; see gate.log", "merge refused: gate red");
        }
        var (_, perr) = work.Publish(session, j);
        if (perr is not null) throw Blocked(session, j, "human", "merge-blocked", perr, $"merge blocked: {perr}");
        var pr = PsJsonReader.Int(j.Node["pr"]?["number"]);
        // A stacked job's PR may still point at its base's branch (the base merged outside the factory).
        if (!string.IsNullOrEmpty(j.Base) && github.Send(HttpMethod.Patch, $"/repos/{j.Target}/pulls/{pr}", new JsonObject { ["base"] = "main" }) is { Status: not 200 } rb)
            throw Blocked(session, j, "human", "merge-blocked", GitHubErrors.Text(rb), $"merge blocked: {GitHubErrors.Text(rb)}");
        // sha pins the gated head: GitHub refuses the merge if the branch moved since.
        var res = github.Send(HttpMethod.Put, $"/repos/{j.Target}/pulls/{pr}/merge", new JsonObject
        {
            ["merge_method"] = "merge", ["sha"] = PsJsonReader.Str(j.Node["gate"]?["sha"]),
            ["commit_title"] = $"Merge factory/{j.Id}: {PsJsonReader.Text(j.Node["title"])}",
        });
        if (res.Status != 200) throw Blocked(session, j, "human", "merge-blocked", GitHubErrors.Text(res), $"merge blocked: {GitHubErrors.Text(res)}");
        return CommandResult.Ok([.. work.CompleteMerge(session, j, PsJsonReader.Text((res.Body as JsonObject)?["sha"]))]);
    }

    /// <summary>The job stays in ready, flagged; the command refuses.</summary>
    private FactoryException Blocked(IBoardSession session, Job j, string by, string reason, string detail, string refusal)
    {
        BoardMoves.Move(session, j, Stages.Ready, by, clock, reason, detail);
        return new FactoryException(refusal);
    }
}
