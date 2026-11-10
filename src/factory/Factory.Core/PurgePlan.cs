using System.Text.Json.Nodes;

namespace Factory.Core;

/// <summary>
/// What <c>purge</c> removes: done and discarded jobs that have been evaluated (an unevaluated job's run logs are
/// what its evaluation is written from), and finished ideas none of whose jobs are left.
/// </summary>
public sealed record PurgePlan(IReadOnlyList<Job> Jobs, IReadOnlyList<Idea> Ideas)
{
    /// <summary>The ids it removes: ideas first, then jobs, as purge prints them.</summary>
    public IReadOnlyList<string> Gone => [.. Ideas.Select(i => i.Id), .. Jobs.Select(j => j.Id)];

    public static PurgePlan For(JsonObject board, Func<string, bool> evaluated)
    {
        var jobs = board.Jobs().Where(j => PsJsonReader.In(j.Stage, Stages.Done, Stages.Discarded) && evaluated(j.Id)).ToList();
        var kept = board.Jobs().Select(j => j.Id).Where(id => !jobs.Any(g => PsJsonReader.Is(g.Id, id))).ToList();
        var ideas = board.Ideas()
            .Where(i => PsJsonReader.In(i.Status, "done", "failed", "abandoned"))
            .Where(i => !(i.Node["jobs"] is JsonArray a ? a.Select(PsJsonReader.Text) : []).Any(id => kept.Any(k => PsJsonReader.Is(k, id))))
            .ToList();
        return new PurgePlan(jobs, ideas);
    }

    /// <summary>Takes the plan's jobs and ideas off the board, recording the highest ids in <c>seq</c> first.</summary>
    public void Apply(JsonObject board)
    {
        Ids.UpdateSeq(board, "J", (JsonArray)board["jobs"]!);
        Ids.UpdateSeq(board, "I", (JsonArray)board["ideas"]!);
        var gone = Gone;
        board["jobs"] = new JsonArray([.. board.Jobs().Where(j => !gone.Any(g => PsJsonReader.Is(g, j.Id))).Select(j => (JsonNode)j.Node.DeepClone())]);
        board["ideas"] = new JsonArray([.. board.Ideas().Where(i => !gone.Any(g => PsJsonReader.Is(g, i.Id))).Select(i => (JsonNode)i.Node.DeepClone())]);
    }
}
