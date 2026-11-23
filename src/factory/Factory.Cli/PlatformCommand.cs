using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>platform status|start|sleep</c> (contract, "The platform"): <c>start</c> waits for Ready with the board lock
/// released; <c>sleep</c> is refused while anything uses the server, naming the first holder.
/// </summary>
public sealed class PlatformCommand(IPlatform platform, IFileStore files, IFactoryPaths paths, TimeProvider clock) : ICommand
{
    public string Name => "platform";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        switch (args.Arg(1))
        {
            case "status":
                return CommandResult.Ok(platform.Status());
            case "start":
                var said = "";
                session.Unlocked(() => said = platform.Start());
                return CommandResult.Ok(said);
            case "sleep":
                if (platform.Status() == "asleep") return CommandResult.Ok("platform: asleep");
                if (Holder(session.Board) is { } holder) throw new FactoryException($"platform busy: {holder}");
                return CommandResult.Ok(platform.Sleep());
            default:
                throw new FactoryException("platform refused: usage: platform status|start|sleep");
        }
    }

    /// <summary>The first thing that uses the server, in the contract's order, or null.</summary>
    private string? Holder(JsonObject board)
    {
        if (files.ReadText(Path.Combine(paths.Home, "canary.json")) is { } text && JsonNode.Parse(text) is JsonObject c)
        {
            if (PsJsonReader.Is(PsJsonReader.Str(c["status"]), "running")) return "the canary is running";
            if (c["teardown"] is JsonObject t && PsJsonReader.Is(PsJsonReader.Str(t["status"]), "destroying")) return "the canary is tearing down";
        }
        var jobs = board.Jobs().ToList();
        if (jobs.FirstOrDefault(j => PsJsonReader.In(j.Stage, Stages.Canary, Stages.Deploy)) is { } held)
            return $"{held.Id} is in {held.Stage}";
        var now = clock.GetUtcNow();
        foreach (var j in jobs)
        {
            if (j.Node["deploy"] is not JsonObject d) continue;
            var status = PsJsonReader.Str(d["status"]);
            if (!PsJsonReader.In(status, "waking", "applying", "up", "destroying")) continue;
            if (DateTimeOffset.TryParse(PsJsonReader.Str(d["destroyAt"]), out var at) && at <= now) continue;
            return $"{j.Id}'s preview is {status}";
        }
        return null;
    }
}
