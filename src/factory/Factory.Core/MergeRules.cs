using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>Why <c>merge</c> cannot go ahead: the flag's detail (<c>merge-blocked</c>) and the refusal it prints.</summary>
public sealed record MergeBlock(string Detail, string Refusal);

/// <summary>The gate and merge preconditions, and stacked jobs (contract, "Gate and merge", "Stacked jobs").</summary>
public static partial class MergeRules
{
    /// <summary>A gate is green when every recorded command exited 0, at the branch's current head.</summary>
    public static bool GateGreen(Job job, string? head)
    {
        if (job.Node["gate"] is not JsonObject gate) return false;
        foreach (var (key, value) in gate)
        {
            if (Json.In(key, "sha", "at")) continue;
            var zero = value is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? Json.Number(v) == 0 : Json.Text(value) == "0";
            if (!zero) return false;
        }
        return Json.Is(Json.Str(gate["sha"]), head);
    }

    /// <summary>The job <paramref name="job"/> is stacked on, or null. A base purged from the board was finished, so it counts as merged.</summary>
    public static Job? BaseJob(JsonObject board, Job job) => string.IsNullOrEmpty(job.Base) ? null : board.FindJob(job.Base);

    /// <summary>Where the job's branch starts and its PR points: the base's branch while the base is unmerged, else main.</summary>
    public static string BaseBranch(JsonObject board, Job job) =>
        BaseJob(board, job) is { } b && !Json.Is(b.Stage, Stages.Done) ? $"factory/{b.Id}" : "main";

    /// <summary>Why job <paramref name="id"/> cannot be a base for work on <paramref name="target"/>, or null.</summary>
    public static string? BaseError(JsonObject board, string target, string id)
    {
        if (board.FindJob(id) is not { } b) return $"no job {id}";
        if (!Json.Is(b.Target, target)) return $"{id} is on {b.Target}, not {target}";
        if (Json.In(b.Stage, Stages.Done, Stages.Discarded)) return $"{id} is {b.Stage}; start from main";
        if (!Json.Truthy(b.Node["pr"])) return $"{id} has no PR yet, so there is no branch to build on";
        return null;
    }

    /// <summary><c>merge</c> refuses a stacked job while its base is not done.</summary>
    public static void RequireBaseMerged(JsonObject board, Job job)
    {
        if (BaseJob(board, job) is { } b && !Json.Is(b.Stage, Stages.Done))
            throw new FactoryException($"merge refused: {job.Id} is stacked on {b.Id}; merge {b.Id} first");
    }

    /// <summary><c>discard</c> refuses a job that live jobs (not done or discarded) are stacked on.</summary>
    public static void RequireNothingStacked(JsonObject board, Job job)
    {
        var kids = board.Jobs().Where(k => Json.Is(k.Base, job.Id) && !Json.In(k.Stage, Stages.Done, Stages.Discarded)).Select(k => k.Id).ToList();
        if (kids.Count > 0)
            throw new FactoryException($"discard refused: {string.Join(", ", kids)} stacked on {job.Id}; discard them first, or merge {job.Id}");
    }

    /// <summary>
    /// Merge step 0: uncommitted changes in the work dir (<paramref name="dirty"/>, <c>git diff --name-only HEAD</c>)
    /// or no commits beyond main (<paramref name="ahead"/>, <c>git rev-list --count</c>) block the merge. Null when neither.
    /// </summary>
    public static MergeBlock? WorkBlock(string jobId, string dirty, string ahead)
    {
        if (dirty.Length > 0)
            return new MergeBlock($"uncommitted changes in the work dir: {string.Join(", ", NewLines().Split(dirty))}", "merge blocked: uncommitted changes");
        if (!Ahead().IsMatch(ahead))
            return new MergeBlock($"nothing to merge: factory/{jobId} adds no commits to main", "merge blocked: nothing to merge");
        return null;
    }

    [GeneratedRegex(@"\r?\n")] private static partial Regex NewLines();
    [GeneratedRegex(@"^[1-9]\d*$")] private static partial Regex Ahead();
}

/// <summary>Board ids: <c>I-NNN</c> and <c>J-NNN</c>, one above the larger of <c>seq</c> and the highest id on the board.</summary>
public static partial class Ids
{
    /// <summary>Allocates the next id and records it in <c>board.seq</c>, so a purged id is never reused.</summary>
    public static string Next(JsonObject board, string prefix, JsonArray items)
    {
        if (board["seq"] is not JsonObject seq) board["seq"] = seq = [];
        var max = Json.Int(seq[prefix]);
        var id = new Regex($"^{Regex.Escape(prefix)}-(\\d+)$", RegexOptions.IgnoreCase);
        foreach (var x in items)
            if (id.Match(Json.Text(x?["id"])) is { Success: true } m) max = Math.Max(max, int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        seq[prefix] = max + 1;
        return $"{prefix}-{max + 1:D3}";
    }
}
