using System.Text;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>lesson-mark &lt;id&gt; open|proposed|applied [-Detail &lt;session&gt;]</c>: a lesson's status as the dashboard moves it
/// toward a template fix, and the session that proposed it.
/// </summary>
public sealed class LessonMarkCommand(IFactoryPaths paths, IFileStore files) : ICommand
{
    public string Name => "lesson-mark";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var id = args.Arg(1);
        if (!EvalRating.IsJobId(id)) throw new FactoryException($"lesson-mark refused: '{id}' is not a job id");
        var status = args.Arg(2);
        if (!PsJsonReader.In(status, "open", "proposed", "applied")) throw new FactoryException("lesson-mark: status must be open, proposed or applied");
        var path = Path.Combine(paths.Home, "lessons", $"{id}.json");
        if (files.ReadText(path) is not { } json) throw new FactoryException($"lesson-mark refused: {id} has no lessons");
        var info = JsonNode.Parse(json)!.AsObject();
        info["status"] = status;
        if (args.Named("Detail") is { Length: > 0 } detail) info["session"] = detail;
        files.WriteAtomic(path, Encoding.UTF8.GetBytes(PsJson.Write(info, Environment.NewLine)));
        return CommandResult.Ok();
    }
}
