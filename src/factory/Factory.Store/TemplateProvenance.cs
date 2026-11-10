using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// factory.ps1's Get-Provenance: the paths this round's own commits changed (base work merged in excluded) whose
/// adding commit came from a template, that is reachable from the target's bootstrap merge, or a <c>scaffold:</c>
/// or <c>addon:</c> commit.
/// </summary>
public sealed partial class TemplateProvenance(IFactoryPaths paths, IFileStore files, IGitClient git) : ITemplateProvenance
{
    private static readonly StringComparer PsOrder = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);

    public Provenance For(JsonObject board, Job job)
    {
        var wt = paths.WorkDir(job.Id);
        var roundStart = PsJsonReader.Str(job.Node["roundStart"]);
        if (string.IsNullOrEmpty(roundStart) || !IsClone(wt))
            return new Provenance(null, $"no round start recorded for {job.Id}");
        var boot = board.Jobs()
            .Where(j => PsJsonReader.Is(PsJsonReader.Str(j.Node["kind"]), "bootstrap") && PsJsonReader.Is(j.Target, job.Target) && PsJsonReader.Truthy(j.Node["merged"]))
            .Select(j => PsJsonReader.Text(j.Node["merged"]?["sha"])).ToList();
        if (boot.Count == 0) boot = [.. EvaluatedBootstraps(job.Target)];
        var note = boot.Count == 0 ? $"no bootstrap merge on record for {job.Target}: only scaffold: and addon: commits count as template-sourced" : null;
        // `--not <base>`, not `^<base>`: the caret does not survive the trip through Invoke-Git intact.
        string[] excl = git.Run(wt, "rev-parse", "--verify", "--quiet", "refs/remotes/factory/base").Code == 0 ? ["--not", "refs/remotes/factory/base"] : [];
        var changed = git.Run(wt, ["-c", "core.quotePath=false", "log", "--no-merges", "--format=", "--name-only", $"{roundStart}..HEAD", .. excl]).Out
            .Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, PsOrder);
        var found = new List<string>();
        foreach (var p in changed)
        {
            var add = git.Run(wt, "log", "--diff-filter=A", "--format=%H", "--", p).Out.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            if (add is null) continue;
            var subject = git.Run(wt, "log", "-1", "--format=%s", add).Out;
            if (TemplateCommit().IsMatch(subject) || boot.Any(b => git.Run(wt, "merge-base", "--is-ancestor", add, b).Code == 0)) found.Add(p);
        }
        return new Provenance(found, note);
    }

    /// <summary>Test-Path &lt;wt&gt;/.git: a clone's .git folder (or a worktree's .git file).</summary>
    private bool IsClone(string wt) => files.DirectoryExists(Path.Combine(wt, ".git")) || files.Stat(Path.Combine(wt, ".git")) is not null;

    /// <summary>A purged bootstrap job leaves its merge sha in its eval.</summary>
    private IEnumerable<string> EvaluatedBootstraps(string? target)
    {
        foreach (var f in files.Files(Path.Combine(paths.Home, "evals")))
        {
            var name = Path.GetFileName(f.Path);
            if (Path.GetDirectoryName(f.Path) != Path.GetFullPath(Path.Combine(paths.Home, "evals")) || !EvalFile().IsMatch(name)) continue;
            JsonObject? e;
            try { e = JsonNode.Parse(files.ReadText(f.Path) ?? "") as JsonObject; } catch (JsonException) { continue; }
            if (e is not null && PsJsonReader.Is(PsJsonReader.Str(e["kind"]), "bootstrap") && PsJsonReader.Is(PsJsonReader.Str(e["target"]), target) && PsJsonReader.Truthy(e["merged"]))
                yield return PsJsonReader.Text(e["merged"]);
        }
    }

    [GeneratedRegex("^(scaffold|addon):", RegexOptions.IgnoreCase)] private static partial Regex TemplateCommit();
    [GeneratedRegex(@"^J-.*\.json$", RegexOptions.IgnoreCase)] private static partial Regex EvalFile();
}
