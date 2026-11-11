using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>sync-prs</c>: ready jobs whose PR the human merged on GitHub go to done; prints each one's id.</summary>
public sealed class SyncPrsCommand(IJobWork work) : ICommand
{
    public string Name => "sync-prs";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var lines = new List<string>();
        foreach (var j in session.Board.Jobs().Where(j => PsJsonReader.Is(j.Stage, Stages.Ready) && PsJsonReader.Truthy(j.Node["pr"])).ToList())
        {
            if (work.GitHubMerge(j) is not { } pr) continue;
            lines.Add(j.Id);
            lines.AddRange(work.CompleteGitHubMerge(session, j, pr));
        }
        return new CommandResult(0, lines, []);
    }
}
