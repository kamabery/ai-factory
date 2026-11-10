using System.Globalization;
using System.Text.RegularExpressions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>The order the cap deletes in (contract, "State backends", Cap). What it never offers is never deleted.</summary>
public static partial class PrunePlan
{
    public const int BoardVersionsKept = 50;

    /// <summary>
    /// Every deletable row, first to delete first: 1. old versions (board.json's beyond the newest 50); 2. the bulky
    /// logs of jobs that are done, discarded or purged, oldest first; 3. the rest of those jobs; 4. sessions whose
    /// turn lease is free, oldest session.json first.
    /// </summary>
    public static IReadOnlyList<BlobItem> Order(IReadOnlyList<BlobItem> rows, IEnumerable<Job> jobs)
    {
        var order = new List<BlobItem>();
        var boardOld = rows.Where(r => r.Name == "board.json" && !r.IsCurrent)
            .OrderByDescending(r => r.VersionId, StringComparer.Ordinal).Skip(BoardVersionsKept);
        order.AddRange(rows.Where(r => !r.IsCurrent && r.Name != "board.json").OrderBy(r => r.LastModified).Concat(boardOld));

        var byId = new Dictionary<string, Job>(StringComparer.OrdinalIgnoreCase);
        foreach (var j in jobs) byId.TryAdd(j.Id, j);
        var finished = new List<(DateTime At, List<BlobItem> Rows)>();
        foreach (var g in rows.Where(r => JobRow().IsMatch(r.Name)).GroupBy(r => r.Name.Split('/')[1], StringComparer.OrdinalIgnoreCase))
        {
            var known = byId.TryGetValue(g.Key, out var j);
            if (known && !string.Equals(j!.Stage, Stages.Done, StringComparison.OrdinalIgnoreCase) && !string.Equals(j.Stage, Stages.Discarded, StringComparison.OrdinalIgnoreCase)) continue;
            var at = known
                ? DateTime.Parse(j!.Node["updatedAt"]?.ToString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
                : g.Max(r => r.LastModified);
            finished.Add((at, [.. g]));
        }
        finished = [.. finished.OrderBy(f => f.At)];
        foreach (var f in finished) order.AddRange(f.Rows.Where(r => Bulky().IsMatch(r.Name)));
        foreach (var f in finished) order.AddRange(f.Rows.Where(r => !Bulky().IsMatch(r.Name)));

        var leased = rows.Where(r => r.IsCurrent && r.Leased && r.Name.StartsWith("locks/turn-", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name["locks/turn-".Length..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sessions = rows.Where(r => SessionRow().IsMatch(r.Name)).GroupBy(r => r.Name.Split('/')[1], StringComparer.OrdinalIgnoreCase)
            .Where(g => !leased.Contains(g.Key))
            .Select(g => (At: g.FirstOrDefault(r => r.IsCurrent && r.Name.EndsWith("/session.json", StringComparison.OrdinalIgnoreCase))?.LastModified ?? DateTime.MinValue, Rows: g))
            .OrderBy(s => s.At);
        foreach (var s in sessions) order.AddRange(s.Rows);

        return [.. order.Distinct()];
    }

    [GeneratedRegex(@"^jobs/([^/]+)/", RegexOptions.IgnoreCase)] private static partial Regex JobRow();
    [GeneratedRegex(@"^sessions/([^/]+)/", RegexOptions.IgnoreCase)] private static partial Regex SessionRow();
    [GeneratedRegex(@"^jobs/[^/]+/(run-[^/]*\.jsonl|transcripts/.*|deploy-[^/]*\.log|gate\.log)$", RegexOptions.IgnoreCase)] private static partial Regex Bulky();
}
