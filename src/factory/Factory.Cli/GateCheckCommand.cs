using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary>
/// <c>gate-check &lt;id&gt; [name]</c>: an agent's dry run of the gate in the work dir, changing nothing on the board. Prints
/// each command's output and exits with the first non-zero code. A clean tree's results are cached per head sha
/// (<c>gate-check.json</c>), and concurrent calls take turns on <c>gate-check.lock</c>.
/// </summary>
public sealed class GateCheckCommand(
    IFactoryPaths paths, IFileStore files, IGitClient git, IGateShell shell, IJobWork work, IOptions<FactoryOptions> options,
    TimeProvider clock) : ICommand
{
    public string Name => "gate-check";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        Transitions.RequireStage(j, [Stages.Build, Stages.Review, Stages.Deploy, Stages.Approve], "gate-check");
        if (work.EnsureClone(session.Board, j.Id) is { } err) throw new FactoryException($"gate-check refused: {err}");
        var only = args.Arg(2);
        var cmds = work.GateCommands(j).Where(c => only.Length == 0 || PsJsonReader.Is(c.Name, only)).ToList();
        if (cmds.Count == 0)
            throw new FactoryException($"gate-check refused: {(only.Length > 0 ? $"no gate command named '{only}'" : "no gate commands in the spec")}");
        var wt = paths.WorkDir(j.Id);
        var dir = paths.JobDir(j.Id);
        files.EnsureDirectory(dir);
        var lines = new List<string>();
        var first = 0;
        var timedOut = false;
        session.Unlocked(() =>
        {
            using var held = Lock(Path.Combine(dir, "gate-check.lock"));
            if (held is null) { timedOut = true; return; }
            first = Check(j, cmds, wt, dir, lines);
        });
        if (timedOut) throw new FactoryException($"gate-check refused: another gate-check held {j.Id}'s lock for too long");
        return new CommandResult(first, lines, []);
    }

    private int Check(Job j, List<GateCommand> cmds, string wt, string dir, List<string> lines)
    {
        var cachePath = Path.Combine(dir, "gate-check.json");
        var sha = git.Run(wt, "rev-parse", "HEAD").Out;
        var clean = git.Run(wt, "status", "--porcelain").Out.Length == 0;
        var cache = ReadCache(cachePath);
        if (cache is null || !PsJsonReader.Is(PsJsonReader.Str(cache["sha"]), sha))
            cache = new JsonObject { ["sha"] = sha, ["results"] = new JsonObject() };
        var results = cache["results"] as JsonObject ?? [];
        cache["results"] = results;
        var first = 0;
        lines.Add($"# gate-check {j.Id} at {Iso.Format(clock.GetUtcNow())}");
        foreach (var c in cmds)
        {
            var hit = clean ? results[c.Name] as JsonObject : null;
            var r = hit is not null ? new ShellResult(PsJsonReader.Int(hit["code"]), PsJsonReader.Text(hit["out"])) : shell.Run(c, wt);
            if (hit is null && clean) results[c.Name] = new JsonObject { ["code"] = r.Code, ["out"] = r.Out };
            lines.Add($"\n## {c.Name}: {c.Cmd}");
            lines.Add(r.Out.TrimEnd());
            lines.Add($"exit {r.Code}{(hit is not null ? $" (cached at {sha[..Math.Min(7, sha.Length)]})" : "")}");
            if (r.Code != 0 && first == 0) first = r.Code;
        }
        if (clean) files.WriteAtomic(cachePath, Encoding.UTF8.GetBytes(PsJson.Write(cache, Environment.NewLine)));
        files.WriteAtomic(Path.Combine(dir, "gate-check.log"), Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + Environment.NewLine))));
        return first;
    }

    private JsonObject? ReadCache(string path)
    {
        try { return files.ReadText(path) is { } text ? JsonNode.Parse(text) as JsonObject : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Enter-GateCheckLock: waits up to FACTORY_GATE_CHECK_WAIT_MINUTES (default 20); null on timeout.</summary>
    private IDisposable? Lock(string path)
    {
        var minutes = options.Value.GateCheckWaitMinutes is { Length: > 0 } m ? double.Parse(m, CultureInfo.InvariantCulture) : 20;
        var until = clock.GetUtcNow().AddMinutes(minutes);
        while (true)
        {
            if (files.TryLock(path) is { } held) return held;
            if (clock.GetUtcNow() > until) return null;
            Thread.Sleep(200);
        }
    }
}
