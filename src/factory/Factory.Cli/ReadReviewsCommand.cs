using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>read-reviews &lt;id&gt;</c>: the four verdicts of the current round decide where the job goes; prints its stage.
/// A missing verdict saves the verdicts read and exits 2.
/// </summary>
public sealed class ReadReviewsCommand(IFactoryPaths paths, IFileStore files, IDotEnv dotEnv, TimeProvider clock) : ICommand
{
    public string Name => "read-reviews";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Review], "read-reviews");
        var dir = Path.Combine(paths.JobDir(j.Id), $"round-{j.Round}");
        IReadOnlyList<System.Text.Json.Nodes.JsonObject> events;
        try
        {
            events = Verdicts.ReadReviews(j, role => SpecText.FirstLine(files.ReadText(Path.Combine(dir, $"review-{role}.md"))),
                dotEnv.Get("HCP_ORG") is not null, clock.GetUtcNow());
        }
        catch (FactoryException e) when (e.KeepChanges)
        {
            session.Save();
            throw;
        }
        session.Save();
        foreach (var e in events) session.AddEvent(e);
        return CommandResult.Ok(j.Stage);
    }
}
