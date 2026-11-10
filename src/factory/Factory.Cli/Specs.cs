using System.Text;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>factory.ps1's Add-Job, with its files: the numbered spec in the job's folder, the board, then the stage event.</summary>
public static class Specs
{
    public static string Add(IFactoryPaths paths, IFileStore files, IBoardSession session, Idea idea, IReadOnlyList<string> lines,
        string source, string kind, TimeProvider clock)
    {
        var n = Backlog.AddJob(session.Board, idea, lines, source, kind, clock.GetUtcNow());
        var dir = paths.JobDir(n.Job.Id);
        files.EnsureDirectory(dir);
        files.WriteAtomic(Path.Combine(dir, "spec.md"), new UTF8Encoding(false).GetBytes(PsText.Content(n.Spec, Environment.NewLine)));
        session.Save();
        session.AddEvent(n.Event);
        return n.Job.Id;
    }
}
