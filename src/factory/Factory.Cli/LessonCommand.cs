using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary>
/// <c>lesson &lt;id&gt; [-File &lt;eval.md&gt;]</c>: what a job that went bad taught, for the templates (contract, "Lessons"):
/// its flag, each round's deploy.md and build.md <c>## Template gaps</c> (less "None" and the paths the builder judged
/// job-specific), and the evaluation. Writes <c>lessons/&lt;id&gt;.md</c> and <c>.json</c> (keeping a status already set) and
/// prints the md's path; a clean job writes nothing and prints <c>no lessons</c>.
/// </summary>
public sealed partial class LessonCommand(IFactoryPaths paths, IFileStore files, IOptions<FactoryOptions> options, TimeProvider clock) : ICommand
{
    public string Name => "lesson";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        var file = args.Named("File");
        string? evaluation = null;
        if (file.Length > 0)
            evaluation = files.ReadText(Path.GetFullPath(file, options.Value.WorkingDirectory)) ?? throw new FactoryException($"lesson file not found: {file}");

        var gaps = new List<(string Round, string Text)>();
        foreach (var round in Rounds(j.Id))
        {
            var dir = Path.Combine(paths.JobDir(j.Id), round);
            var deploy = SpecText.Section(files.ReadText(Path.Combine(dir, "deploy.md")), "Template gaps");
            if (deploy is not null && !None().IsMatch(deploy.Trim())) gaps.Add((round, deploy));
            // The builder's too, without the paths it judged job-specific (build checks, template provenance).
            var build = SpecText.Section(files.ReadText(Path.Combine(dir, "build.md")), "Template gaps");
            if (build is null) continue;
            build = string.Join("\n", build.Split('\n').Where(l => !JobSpecific().IsMatch(l))).Trim();
            if (build.Length > 0 && !None().IsMatch(build)) gaps.Add(($"{round}, build", build));
        }
        var count = gaps.Sum(g => g.Text.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
        var flag = j.Node["flag"] as JsonObject;
        if (flag is null && gaps.Count == 0 && evaluation is null) return CommandResult.Ok("no lessons");

        var md = new List<string> { $"# Lessons: {j.Id} - {PsJsonReader.Text(j.Node["title"])}", "", $"- Target: {PsJsonReader.Text(j.Node["target"])}" };
        if (flag is not null)
        {
            md.Add($"- Flag: {PsJsonReader.Text(flag["reason"])}: {PsJsonReader.Text(flag["detail"])}");
            if (PsJsonReader.Truthy(flag["why"])) md.AddRange(["", "## Why it was flagged", PsJsonReader.Text(flag["why"])]);
        }
        foreach (var (round, text) in gaps) md.AddRange(["", $"## Template gaps ({round})", text]);
        if (evaluation is not null) md.AddRange(["", "## From the evaluation", evaluation.Trim()]);

        var lessons = Path.Combine(paths.Home, "lessons");
        files.EnsureDirectory(lessons);
        var path = Path.Combine(lessons, $"{j.Id}.json");
        var old = Read(path);
        var info = new JsonObject
        {
            ["job"] = j.Id, ["title"] = j.Node["title"]?.DeepClone(), ["target"] = j.Node["target"]?.DeepClone(),
            ["flag"] = flag?["reason"]?.DeepClone(), ["templateGaps"] = count,
            ["status"] = old is null ? "open" : old["status"]?.DeepClone(), ["session"] = old?["session"]?.DeepClone(),
            ["at"] = Iso.Format(clock.GetUtcNow()),
        };
        // Set-Content: the lines joined by "`n", then one newline.
        files.WriteAtomic(Path.Combine(lessons, $"{j.Id}.md"), Encoding.UTF8.GetBytes(string.Join("\n", md) + Environment.NewLine));
        files.WriteAtomic(path, Encoding.UTF8.GetBytes(PsJson.Write(info, Environment.NewLine)));
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "lesson"), ("detail", count.ToString(CultureInfo.InvariantCulture))));
        return CommandResult.Ok($"lessons/{j.Id}.md");
    }

    /// <summary>The job's round-* folders, in round order.</summary>
    private IEnumerable<string> Rounds(string id)
    {
        var dir = Path.GetFullPath(paths.JobDir(id));
        return files.Files(dir)
            .Select(f => Path.GetRelativePath(dir, f.Path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(p => p.Length > 1 && RoundDir().IsMatch(p[0]))
            .Select(p => p[0]).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => int.TryParse(Digits().Replace(r, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0);
    }

    private JsonObject? Read(string path)
    {
        try { return files.ReadText(path) is { } t ? JsonNode.Parse(t) as JsonObject : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>"None", written bare or as a bullet ("- None."), is no gap.</summary>
    [GeneratedRegex(@"^(?i)(-\s*)?none\.?$")] private static partial Regex None();
    [GeneratedRegex(@"^- [^\s]+: job-specific\b", RegexOptions.IgnoreCase)] private static partial Regex JobSpecific();
    [GeneratedRegex("^round-", RegexOptions.IgnoreCase)] private static partial Regex RoundDir();
    [GeneratedRegex(@"\D")] private static partial Regex Digits();
}
