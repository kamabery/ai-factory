using System.Globalization;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Cli;

/// <summary>
/// <c>set-cost &lt;id&gt; &lt;usd&gt; &lt;ms&gt;</c>: adds one agent run's cost and duration to the job and appends a <c>cost</c> event
/// carrying the run mode's provider (contract, "Run mode and providers").
/// </summary>
public sealed class SetCostCommand(IDotEnv dotEnv, IOptions<FactoryOptions> options, TimeProvider clock) : ICommand
{
    public string Name => "set-cost";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        if (!double.TryParse(args.Arg(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var usd))
            throw new FactoryException($"set-cost refused: '{args.Arg(2)}' is not a cost in USD");
        var ms = Milliseconds(args.Arg(3));
        // Get-FactoryProvider: FACTORY_MODE from the environment first (a deployed factory sets it there), then .env.
        var provider = RunMode.Provider(options.Value.Mode ?? dotEnv.Get("FACTORY_MODE"));
        var cost = j.Node["costUsd"] is { } c ? double.Parse(c.ToJsonString(), CultureInfo.InvariantCulture) : 0;
        j.Node["costUsd"] = PsNumber.Double(Math.Round(cost + usd, 4));
        j.Node["durationMs"] = (j.Node["durationMs"] is { } d ? long.Parse(d.ToJsonString(), CultureInfo.InvariantCulture) : 0) + ms;
        session.Save();
        session.AddEvent(Events.Create(clock.GetUtcNow(), ("job", j.Id), ("kind", "cost"), ("costUsd", PsNumber.Double(usd)), ("durationMs", ms), ("provider", provider)));
        return CommandResult.Ok();
    }

    /// <summary>PowerShell's <c>[long]</c> cast: "" is 0, and a fraction rounds half to even.</summary>
    private static long Milliseconds(string s)
    {
        if (s.Length == 0) return 0;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)) return ms;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (long)Math.Round(d);
        throw new FactoryException($"set-cost refused: '{s}' is not a duration in ms");
    }
}
