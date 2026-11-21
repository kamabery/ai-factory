using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>
/// The markdown factory.ps1 parses: spec sections, scope globs, report sections and first lines.
/// Its <c>-match</c> ignores case, so these regexes do too.
/// </summary>
public static partial class SpecText
{
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary><c>## Target</c> (<c>- Repo: owner/name</c>, the last one wins) and <c>## Gate</c> (<c>- name: `cmd`</c>).</summary>
    public static SpecMeta Meta(IEnumerable<string> lines)
    {
        string? repo = null;
        var gate = new List<GateCommand>();
        var section = "";
        foreach (var line in lines)
        {
            if (Heading2().Match(line) is { Success: true } h) { section = h.Groups[1].Value.ToLowerInvariant(); continue; }
            if (section == "target" && RepoLine().Match(line) is { Success: true } r) repo = r.Groups[1].Value;
            if (section == "gate" && GateLine().Match(line) is { Success: true } g) gate.Add(new GateCommand(g.Groups[1].Value, g.Groups[2].Value));
        }
        return new SpecMeta(repo, gate);
    }

    /// <summary>
    /// The globs of each sub-bullet under <c>## Scope</c> <c>- In:</c>: its leading backticked spans joined only by commas,
    /// once parentheses are dropped. <c>`CLAUDE.md`, `README.md` (prose)</c> gives both; <c>`a`, and `b` is prose</c> gives
    /// <c>a</c> (contract, "Targets").
    /// </summary>
    public static ScopeIn ScopeIn(IReadOnlyList<string> lines)
    {
        var globs = new List<string>();
        int at = -1;
        bool inScope = false, inIn = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (AnyHeading().Match(line) is { Success: true } h) { inScope = string.Equals(h.Groups[1].Value, "Scope", StringComparison.OrdinalIgnoreCase); inIn = false; continue; }
            if (!inScope) continue;
            if (InLine().IsMatch(line)) { inIn = true; at = i; continue; }
            if (Unindented().IsMatch(line)) { inIn = false; continue; }
            if (inIn && GlobBullet().IsMatch(line))
            {
                var run = LeadingGlobs().Match(Parenthetical().Replace(line, "")).Groups[1].Value;
                globs.AddRange(Backticked().Matches(run).Select(m => m.Groups[1].Value.Trim()));
                at = i;
            }
            else if (inIn && Indented().IsMatch(line)) at = i;
        }
        return new ScopeIn(globs, at);
    }

    /// <summary>
    /// The body of a markdown file's <c>## heading</c> section, trimmed; null when the file or the section is
    /// absent, or the body is empty or <c>N/A</c>.
    /// </summary>
    public static string? Section(string? text, string heading)
    {
        if (text is null) return null;
        var m = Regex.Match(text.Replace("\r", ""), $"^## {Regex.Escape(heading)}[ \\t]*\\n(.*?)(?=^## |\\z)", I | RegexOptions.Multiline | RegexOptions.Singleline);
        if (!m.Success) return null;
        var body = m.Groups[1].Value.Trim();
        return body.Length == 0 || string.Equals(body, "N/A", StringComparison.OrdinalIgnoreCase) ? null : body;
    }

    /// <summary>A file's first line, trimmed at the end; null for a missing or empty file.</summary>
    public static string? FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).TrimEnd();
    }

    /// <summary>Null for a target written owner/name, else why it is refused (contract, "Targets").</summary>
    public static string? TargetError(string? target)
    {
        if (string.IsNullOrEmpty(target)) return "a target repo (owner/name) is required";
        if (!TargetPattern().IsMatch(target)) return $"target '{target}' is not a GitHub repo written owner/name";
        return null;
    }

    [GeneratedRegex(@"^##\s+(.+?)\s*$", I)] private static partial Regex Heading2();
    [GeneratedRegex(@"^\s*-\s*Repo:\s*(.+?)\s*$", I)] private static partial Regex RepoLine();
    [GeneratedRegex(@"^\s*-\s*([a-z0-9-]+):\s*`(.+)`\s*$", I)] private static partial Regex GateLine();
    [GeneratedRegex(@"^#{1,6}\s+(.*?)\s*$", I)] private static partial Regex AnyHeading();
    [GeneratedRegex(@"^[-*]\s*In:", I)] private static partial Regex InLine();
    [GeneratedRegex(@"^\S", I)] private static partial Regex Unindented();
    [GeneratedRegex(@"^\s+[-*]\s+`([^`]+)`", I)] private static partial Regex GlobBullet();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex Backticked();
    [GeneratedRegex(@"^\s+[-*]\s+(`[^`]+`(?:\s*,\s*`[^`]+`)*)", I)] private static partial Regex LeadingGlobs();
    [GeneratedRegex(@"\([^()]*\)")] private static partial Regex Parenthetical();
    [GeneratedRegex(@"^\s+\S", I)] private static partial Regex Indented();
    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$", I)] private static partial Regex TargetPattern();
}
