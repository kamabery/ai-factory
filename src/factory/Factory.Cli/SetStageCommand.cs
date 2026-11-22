using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>set-stage &lt;id&gt; &lt;stage&gt; [-By -Reason -Detail -Session]</c>: the runner and orchestrator move a job through the
/// Building stages (contract, "Job transitions"). approved -> build takes the queue's head; build -> review runs
/// build checks 1 to 3 (scope, base moved, template provenance) and prints the stage the job lands in.
/// </summary>
public sealed partial class SetStageCommand(
    IFactoryPaths paths, IFileStore files, IGitClient git, IDotEnv dotEnv, ICanary canary, ITemplateProvenance provenance,
    TimeProvider clock) : ICommand
{
    private static readonly string[] Resumable = [Stages.Build, Stages.Review, Stages.Deploy, Stages.Approve];

    public string Name => "set-stage";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var board = session.Board;
        var j = board.Job(args.Arg(1));
        var to = args.Arg(2);
        var by = args.Named("By") is { Length: > 0 } b ? b : "orchestrator";
        var deploysOn = dotEnv.Get("HCP_ORG") is not null;
        var now = clock.GetUtcNow();
        var from = j.Stage;
        // factory.ps1's messages name the move as its switch clause spells it: "approved->build", "approved->$to".
        void RequireNext(string move)
        {
            if (!Queue.IsNext(board, j, deploysOn, canary.State)) throw new FactoryException($"{move} refused: {j.Id} is not next in the queue, or a job is already building");
        }

        // A job waiting in Canary leaves it as one in Approved does, once a canary has passed (contract, "Canary").
        var queued = PsJsonReader.In(from, Stages.Approved, Stages.Canary);
        if (queued && PsJsonReader.Truthy(j.Node["resumeSession"]) && PsJsonReader.In(to, Resumable))
        {
            // Queued by resume: back to the stage the usage limit stopped it in, as it was.
            RequireNext($"{from}->{to}");
            if (!PsJsonReader.Is(j.Resume, to)) throw new FactoryException($"{from}->{to} refused: {j.Id} was resumed; it resumes at {j.Resume}");
            var start = j.Node["roundStart"]?.DeepClone();
            j.Node["resume"] = null;
            j.Node["resumeSession"] = null;
            var e = Transitions.Move(j, to, by, null, null, null, now);
            if (PsJsonReader.Truthy(start)) j.Node["roundStart"] = start;
            return Done(session, [e]);
        }
        if (queued && PsJsonReader.Is(to, Stages.Build))
        {
            RequireNext($"{from}->build");
            if (PsJsonReader.Truthy(j.Node["resume"])) throw new FactoryException($"{from}->build refused: {j.Id} was redeployed; it resumes at {j.Resume}");
            Transitions.ResetReviews(j);
            return Done(session, [Transitions.Move(j, Stages.Build, by, null, null, null, now)]);
        }
        if (queued && PsJsonReader.Is(to, Stages.Deploy))
        {
            RequireNext($"{from}->deploy");
            if (!PsJsonReader.Is(j.Resume, Stages.Deploy)) throw new FactoryException($"{from}->deploy refused: {j.Id} was not redeployed");
            j.Node["resume"] = null;
            return Done(session, [Transitions.Move(j, Stages.Deploy, by, null, null, null, now)]);
        }
        if (PsJsonReader.Is(from, Stages.Build) && PsJsonReader.Is(to, Stages.Review))
        {
            var events = BuildToReview(board, j, by, deploysOn, now, session);
            Done(session, events);
            return CommandResult.Ok(j.Stage);
        }

        var detail = args.Named("Detail");
        if (PsJsonReader.Is(to, Stages.Ready) && PsJsonReader.In(from, Resumable) && PsJsonReader.Is(args.Named("Reason"), "usage-limit"))
        {
            var sessionId = args.Named("Session");
            if (sessionId.Length == 0) throw new FactoryException($"usage-limit refused: {j.Id} needs -Session, the claude session to resume");
            var e = Transitions.Move(j, Transitions.EscalationStage(j, deploysOn), by, "usage-limit", detail, null, now);
            var flag = (JsonObject)j.Node["flag"]!;
            flag["stage"] = from;
            flag["session"] = sessionId;
            return Done(session, [e]);
        }
        if (PsJsonReader.Is(to, Stages.Ready) && PsJsonReader.In(from, Stages.Building) && PsJsonReader.Is(args.Named("Reason"), "escalated"))
            return Done(session, [Transitions.Move(j, Transitions.EscalationStage(j, deploysOn), by, "escalated", detail, null, now)]);
        throw new FactoryException($"transition {from}->{to} refused (use the dedicated command, or see FACTORY-CONTRACT.md)");
    }

    private static CommandResult Done(IBoardSession session, IEnumerable<JsonObject> events)
    {
        session.Save();
        foreach (var e in events) session.AddEvent(e);
        return CommandResult.Ok();
    }

    /// <summary>Build checks 1 to 3, then review, or ready / blocked flagged out-of-scope or spec-stale.</summary>
    private List<JsonObject> BuildToReview(JsonObject board, Job j, string by, bool deploysOn, DateTimeOffset now, IBoardSession session)
    {
        var dir = paths.JobDir(j.Id);
        var buildMd = Path.Combine(dir, $"round-{j.Round}", "build.md");
        if (files.Stat(buildMd) is null) throw new FactoryException($"build->review refused: round-{j.Round}/build.md missing");
        var events = new List<JsonObject>();

        // Build check 1, scope: the job's own changes (base work merged in excluded) against Scope: In.
        var globs = SpecText.ScopeIn(PsText.Lines(files.ReadText(Path.Combine(dir, "spec.md")))).Globs;
        IReadOnlyList<string> strays = [];
        if (globs.Count > 0)
        {
            var wt = paths.WorkDir(j.Id);
            if (!files.DirectoryExists(Path.Combine(wt, ".git")) && files.Stat(Path.Combine(wt, ".git")) is null)
                throw new FactoryException($"build->review refused: {j.Id} has no work dir to scope-check");
            var @base = git.Run(wt, "rev-parse", "--verify", "--quiet", "refs/remotes/factory/base").Code == 0 ? "factory/base" : $"origin/{MergeRules.BaseBranch(board, j)}";
            var d = git.Run(wt, "-c", "core.quotePath=false", "diff", "--name-only", $"{@base}...HEAD");
            if (d.Code != 0) throw new FactoryException($"build->review refused: cannot diff {@base}...HEAD: {d.Out}");
            strays = Glob.Strays(globs, d.Out.Split('\n').Select(l => l.TrimEnd('\r')));
        }

        // Build check 2, base moved: build.md must say whether the spec holds against the new main.
        string? stale = null;
        var buildText = files.ReadText(buildMd);
        if (strays.Count == 0 && PsJsonReader.Truthy(j.Node["baseMoved"]))
        {
            var section = SpecText.Section(buildText, "Spec against base");
            var holds = section is not null && SpecHolds().Match(section) is { Success: true } m ? m.Groups[1].Value : null;
            if (holds is null)
                throw new FactoryException($"build->review refused: {j.Id}'s base moved ({PsJsonReader.Text(j.Node["baseMoved"]?["by"])} merged), and round-{j.Round}/build.md has no '## Spec against base' with a 'Spec holds: yes|no' line", 2);
            if (PsJsonReader.Is(holds, "yes")) j.Node["baseMoved"] = null;
            else stale = $"## Spec against base\n{section}".TrimEnd();
        }

        // Build check 3, template provenance (rounds after 1): build.md's Template gaps names each path.
        if (strays.Count == 0 && stale is null && j.Round > 1)
        {
            var prov = provenance.For(board, j);
            if (prov.Paths is null)
                events.Add(Events.Create(now, ("job", j.Id), ("kind", "note"), ("detail", $"not provenance-checked: {prov.Note}")));
            else
            {
                var gaps = SpecText.Section(buildText, "Template gaps") ?? "";
                var unnamed = prov.Paths.Where(p => !gaps.Contains(p, StringComparison.Ordinal)).ToList();
                if (unnamed.Count > 0)
                    throw new FactoryException($"build->review refused: round {j.Round} changed template-sourced paths that build.md's '## Template gaps' does not name (as a gap, or '<path>: job-specific, <why>'): {string.Join(", ", unnamed)}", 2);
            }
        }

        if (strays.Count > 0)
        {
            var n = strays.Count;
            var detail = $"{n} path{(n != 1 ? "s" : "")} outside Scope: In: {string.Join(", ", strays.Take(3))}{(n > 3 ? ", ..." : "")}";
            var why = "Paths outside the spec's Scope: In:\n" + string.Join("\n", strays.Select(s => $"- `{s}`"));
            events.Add(Transitions.Move(j, Transitions.EscalationStage(j, deploysOn), by, "out-of-scope", detail, why, now));
        }
        else if (stale is not null)
        {
            events.Add(Transitions.Move(j, Transitions.EscalationStage(j, deploysOn), by, "spec-stale",
                $"the spec does not hold since {PsJsonReader.Text(j.Node["baseMoved"]?["by"])} merged", stale, now));
        }
        else
        {
            j.Node["reviews"] = new JsonObject { ["security"] = "running", ["code"] = "running", ["ux"] = "running", ["ui"] = "running" };
            events.Add(Transitions.Move(j, Stages.Review, by, null, null, null, now));
            if (globs.Count == 0) events.Add(Events.Create(now, ("job", j.Id), ("kind", "note"), ("detail", "not scope-checked: the spec has no Scope: In globs")));
        }
        return events;
    }

    [GeneratedRegex(@"(?m)^\s*Spec holds:\s*(yes|no)\b", RegexOptions.IgnoreCase)] private static partial Regex SpecHolds();
}
