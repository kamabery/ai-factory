using Factory.Core;

namespace Factory.Cli;

public sealed record CommandResult(int ExitCode, IReadOnlyList<string> Out, IReadOnlyList<string> Err)
{
    public static CommandResult Ok(params string[] lines) => new(0, lines, []);
}

/// <summary>A factory.ps1 command, ported.</summary>
public interface ICommand
{
    string Name { get; }
    CommandResult Run(CommandArgs args);
}

/// <summary>Output settings the composition root reads from the platform.</summary>
public sealed class CliOptions
{
    public string NewLine { get; set; } = "\n";
}

/// <summary>factory.ps1's parameters: positional Command, Arg1..Arg3, then -Name value pairs and switches.</summary>
public sealed class CommandArgs
{
    /// <summary>factory.ps1's [switch] parameters; every other -Name takes a value.</summary>
    public static readonly IReadOnlySet<string> Switches =
        new HashSet<string>(["Failed", "Json", "Force", "Full", "NeedsCanary"], StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private init; } = "status";
    public IReadOnlyList<string> Positional { get; private init; } = [];

    public static CommandArgs Parse(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        var named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Length < 2 || args[i][0] != '-') { positional.Add(args[i]); continue; }
            var name = args[i][1..];
            if (Switches.Contains(name)) { named[name] = null; continue; }
            if (i + 1 >= args.Count) throw new FactoryException($"missing a value for -{name}");
            named[name] = args[++i];
        }
        var a = new CommandArgs
        {
            Command = positional.Count > 0 ? positional[0] : "status",
            Positional = positional.Skip(1).ToList(),
        };
        foreach (var (k, v) in named) a._named[k] = v;
        return a;
    }

    public bool Has(string name) => _named.ContainsKey(name);
    public string? Get(string name) => _named.GetValueOrDefault(name);
}

/// <summary>Dispatches to the command named first, as factory.ps1's switch does (case-insensitive).</summary>
public sealed class CliApp(IEnumerable<ICommand> commands)
{
    public CommandResult Run(IReadOnlyList<string> args)
    {
        try
        {
            var a = CommandArgs.Parse(args);
            var command = commands.FirstOrDefault(c => string.Equals(c.Name, a.Command, StringComparison.OrdinalIgnoreCase))
                ?? throw new FactoryException($"unknown command '{a.Command}'");
            return command.Run(a);
        }
        catch (FactoryException e)
        {
            return new CommandResult(e.ExitCode, [], [$"factory: {e.Message}"]);
        }
    }
}
