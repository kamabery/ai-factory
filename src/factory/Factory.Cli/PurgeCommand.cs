using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>purge</c>: takes evaluated done and discarded jobs, and the finished ideas they leave behind, off the board with
/// their folders and work dirs. Prints what it removed.
/// </summary>
public sealed class PurgeCommand(IFactoryPaths paths, IFileStore files, TimeProvider clock) : ICommand
{
    public string Name => "purge";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var plan = PurgePlan.For(session.Board, id => files.Stat(Path.Combine(paths.Home, "evals", $"{id}.json")) is not null);
        foreach (var j in plan.Jobs) WorkDirs.Remove(paths, files, session, j, clock);
        var gone = plan.Gone;
        if (gone.Count == 0) return CommandResult.Ok();
        plan.Apply(session.Board);
        session.Save();
        foreach (var j in plan.Jobs) TryDelete(paths.JobDir(j.Id));
        foreach (var i in plan.Ideas) TryDelete(Path.Combine(paths.Home, "ideas", i.Id));
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("kind", "note"), ("detail", $"purged {string.Join(", ", gone)}")));
        return new CommandResult(0, gone, []);
    }

    private void TryDelete(string dir)
    {
        try { files.DeleteDirectory(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
