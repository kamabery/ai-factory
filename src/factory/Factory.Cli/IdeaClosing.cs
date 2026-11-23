using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// Not yet keeps the idea open (contract, "Ideas", spec 22): an idea whose session ended while specs waited in
/// <c>backlog</c> is <c>closeWhenDecided</c>, and closes once the last of them is approved or discarded.
/// </summary>
public static class IdeaClosing
{
    /// <summary>The idea's jobs still in <c>backlog</c>.</summary>
    public static IReadOnlyList<string> Waiting(IBoardSession session, string ideaId) =>
        [.. session.Board.Jobs().Where(j => PsJsonReader.Is(j.Idea, ideaId) && PsJsonReader.Is(j.Stage, Stages.Backlog)).Select(j => j.Id)];

    /// <summary>After <paramref name="job"/> left <c>backlog</c>: closes its idea when it was waiting on its last spec.</summary>
    public static void AfterDecided(IBoardSession session, Job job, TimeProvider clock)
    {
        if (job.Idea is not { } id) return;
        var idea = session.Board.Ideas().FirstOrDefault(i => PsJsonReader.Is(i.Id, id));
        if (idea is null || !PsJsonReader.Truthy(idea.Node["closeWhenDecided"]) || Waiting(session, id).Count > 0) return;
        idea.Node["status"] = "done";
        idea.Node.Remove("closeWhenDecided");
        session.Save();
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("idea", id), ("kind", "idea"), ("detail", "done")));
    }
}
