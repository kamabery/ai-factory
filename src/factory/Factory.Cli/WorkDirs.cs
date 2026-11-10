using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>factory.ps1's Remove-WorkDir: a job's clone is deleted, and a locked file is a note, never a failure (purge tries again).</summary>
public static class WorkDirs
{
    public static void Remove(IFactoryPaths paths, IFileStore files, IBoardSession session, Job j, TimeProvider clock)
    {
        var wt = paths.WorkDir(j.Id);
        if (!files.DirectoryExists(wt)) return;
        try { files.DeleteDirectory(wt); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "note"), ("detail", $"work dir not removed: {e.Message}")));
        }
    }
}
