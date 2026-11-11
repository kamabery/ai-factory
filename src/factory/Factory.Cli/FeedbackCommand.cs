using System.Text;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary>
/// <c>feedback &lt;id&gt; -File &lt;feedback.md&gt;</c>: the human's own rating of an evaluated job, kept beside the agent's: the
/// markdown as <c>evals/&lt;id&gt;.feedback.md</c>, and <c>human: { rating, at }</c> in its snapshot.
/// </summary>
public sealed class FeedbackCommand(IFactoryPaths paths, IFileStore files, IOptions<FactoryOptions> options, TimeProvider clock) : ICommand
{
    public string Name => "feedback";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var id = args.Arg(1);
        if (!EvalRating.IsJobId(id)) throw new FactoryException($"feedback refused: '{id}' is not a job id");
        var dir = Path.Combine(paths.Home, "evals");
        var path = Path.Combine(dir, $"{id}.json");
        if (files.ReadText(path) is not { } json) throw new FactoryException($"feedback refused: {id} has no evaluation");
        var file = args.Named("File");
        var full = file.Length == 0 ? null : Path.GetFullPath(file, options.Value.WorkingDirectory);
        var text = full is null ? null : files.ReadText(full);
        if (text is null) throw new FactoryException($"feedback file not found: {file}");
        var rating = EvalRating.Of(SpecText.FirstLine(text)) ?? throw new FactoryException($"no 'Rating: good|mixed|poor' first line in {file}", 2);
        var snap = JsonNode.Parse(json)!.AsObject();
        snap["human"] = new JsonObject { ["rating"] = rating, ["at"] = Iso.Format(clock.GetUtcNow()) };
        files.CopyFile(full!, Path.Combine(dir, $"{id}.feedback.md"));
        files.WriteAtomic(path, Encoding.UTF8.GetBytes(PsJson.Write(snap, Environment.NewLine)));
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", id), ("kind", "feedback"), ("detail", rating)));
        return CommandResult.Ok();
    }
}
