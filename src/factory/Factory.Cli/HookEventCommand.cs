using Factory.Cli.Abstractions;
using Factory.Core;

namespace Factory.Cli;

/// <summary>
/// <c>event</c> with no arguments: the plugin's SubagentStart / SubagentStop hook, its payload on stdin (contract,
/// "factory commands"). It appends the event through <c>event &lt;json&gt;</c>, under the board lock, and prints nothing and
/// exits 0 whatever happens, so a hook can never break a session.
/// </summary>
public sealed class HookEventCommand(CliEnvironment host, CliApp app) : IStandaloneCommand
{
    public string Name => "event";

    public bool Accepts(IReadOnlyList<string> args) => args.Count == 0;

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var job = host.Get("FACTORY_JOB");
            var idea = host.Get("FACTORY_IDEA");
            if (job is null && idea is null) return 0;
            if (HookEvent.From(stdin.ReadToEnd(), job, idea) is { } e) app.Run(["event", e.ToJsonString()]);
        }
        catch (Exception) { }
        return 0;
    }
}
