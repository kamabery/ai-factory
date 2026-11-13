using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary>
/// <c>deploy-fix &lt;id&gt; -File &lt;diagnosis&gt;</c>: the devops agent judged the change at fault, so the job goes back to the
/// builder as a new round with its diagnosis; the third time blocks it deploy-fix-rounds instead (contract, "The deploy loop").
/// </summary>
public sealed class DeployFixCommand(IFactoryPaths paths, IFileStore files, IDotEnv dotEnv, IOptions<FactoryOptions> options, TimeProvider clock) : ICommand
{
    public string Name => "deploy-fix";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        DeployRules.RequireFailedDeploy(j, "deploy-fix");
        var file = args.Named("File");
        var full = file.Length == 0 ? null : Path.GetFullPath(file, options.Value.WorkingDirectory);
        var text = full is null ? null : files.ReadText(full);
        if (text is null) throw new FactoryException($"deploy-fix refused: diagnosis file not found: {file}");
        if (PsJsonReader.Int(j.Node["deployFixes"]) >= Preview.MaxDeployFixes)
        {
            var first = SpecText.FirstLine(text)?.TrimEnd();
            BoardMoves.Move(session, j, Transitions.EscalationStage(j, dotEnv.Get("HCP_ORG") is not null), "orchestrator", clock, "deploy-fix-rounds", first);
            return CommandResult.Ok(j.Stage);
        }
        var round = j.Round + 1;
        j.Node["round"] = round;
        files.CopyFile(full!, Path.Combine(paths.JobDir(j.Id), $"deploy-fix-{round}.md"));
        j.Node["deployFixes"] = PsJsonReader.Int(j.Node["deployFixes"]) + 1;
        Transitions.ResetReviews(j);
        j.Node["deploy"]!["attempts"] = 0;
        BoardMoves.Move(session, j, Stages.Build, "orchestrator", clock);
        return CommandResult.Ok(j.Stage);
    }
}
