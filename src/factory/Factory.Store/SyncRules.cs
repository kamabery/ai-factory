using System.Text.RegularExpressions;

namespace Factory.Store;

/// <summary>Which FACTORY_HOME paths the container mirrors (contract, "State backends", Layout). Paths are relative, with <c>/</c>.</summary>
public static partial class SyncRules
{
    private static readonly string[] OwnSync = ["board.json", "events.jsonl", "headless-settings.json", ".sync.json"];

    /// <summary>
    /// Whether push and pull carry the path. board.json and events.jsonl have their own sync; temp and lock files,
    /// the container's <c>events/</c> and <c>locks/</c>, and a machine's <c>.local/</c> files are never synced.
    /// </summary>
    public static bool IsSynced(string rel)
    {
        if (OwnSync.Contains(rel, StringComparer.OrdinalIgnoreCase)) return false;
        return !(rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("events/", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("locks/", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(".local/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The job whose run log (<c>jobs/&lt;id&gt;/run-*.jsonl</c>) this is, or null.</summary>
    public static string? RunLogJob(string rel) => RunLog().Match(rel) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^jobs/([^/]+)/run-[^/]*\.jsonl$", RegexOptions.IgnoreCase)] private static partial Regex RunLog();
}
