using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>factory.ps1's Add-Job: a spec becomes a backlog job of its idea.</summary>
public static partial class Backlog
{
    /// <summary>
    /// Adds the job to the board and the idea, numbering the spec's <c># Spec:</c> heading. The title is the heading
    /// after any <c>id —</c> prefix; the target is the spec's <c>## Target</c> Repo.
    /// </summary>
    public static NewJob AddJob(JsonObject board, Idea idea, IReadOnlyList<string> lines, string source, string kind, DateTimeOffset now)
    {
        var h = lines.ToList().FindIndex(l => SpecHeading().IsMatch(l));
        if (h < 0) throw new FactoryException($"no '# Spec:' heading in {source}");
        var title = SpecPrefix().Replace(lines[h], "").Trim();
        if (IdPrefix().Match(title) is { Success: true } m) title = m.Groups[1].Value.Trim();
        if (title.Length == 0) throw new FactoryException($"empty title in {source}");
        var jobs = board["jobs"] as JsonArray ?? [];
        var id = Ids.Next(board, "J", jobs);
        var spec = lines.ToList();
        spec[h] = $"# Spec: {id} — {title}";
        var ts = Iso.Format(now);
        var node = new JsonObject
        {
            ["id"] = id, ["idea"] = idea.Id, ["kind"] = kind, ["title"] = title, ["stage"] = Stages.Backlog, ["round"] = 1,
            ["branch"] = $"factory/{id}", ["base"] = idea.Node["base"]?.DeepClone(), ["target"] = SpecText.Meta(spec).Repo,
            ["pr"] = null,
            ["reviews"] = new JsonObject { ["security"] = "pending", ["code"] = "pending", ["ux"] = "pending", ["ui"] = "pending" },
            ["decision"] = null, ["gate"] = null, ["flag"] = null, ["merged"] = null, ["approvedAt"] = null,
            ["costUsd"] = null, ["durationMs"] = null, ["createdAt"] = ts, ["updatedAt"] = ts, ["history"] = new JsonArray(),
        };
        board["jobs"] = jobs;
        jobs.Add(node);
        if (idea.Node["jobs"] is JsonArray ideaJobs) ideaJobs.Add(id); else idea.Node["jobs"] = new JsonArray(id);
        var e = Events.Create(now, ("job", id), ("kind", "stage"), ("from", null), ("to", Stages.Backlog), ("by", "orchestrator"), ("idea", idea.Id));
        return new NewJob(new Job(node), spec, e);
    }

    [GeneratedRegex(@"^#\s*Spec:", RegexOptions.IgnoreCase)] private static partial Regex SpecHeading();
    [GeneratedRegex(@"^#\s*Spec:\s*", RegexOptions.IgnoreCase)] private static partial Regex SpecPrefix();
    [GeneratedRegex(@"\s[—-]\s(.+)$", RegexOptions.IgnoreCase)] private static partial Regex IdPrefix();
}
