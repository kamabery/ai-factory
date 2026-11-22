using Factory.Core;

namespace Factory.Cli;

/// <summary>factory.ps1's parameters: positional Command, Arg1..Arg3, then -Name value pairs and switches.</summary>
public sealed class CommandArgs
{
    /// <summary>factory.ps1's [switch] parameters; every other -Name takes a value.</summary>
    public static readonly IReadOnlySet<string> Switches =
        new HashSet<string>(["Failed", "Json", "Force", "Full", "NeedsCanary", "Azure", "Retry", "Due"], StringComparer.OrdinalIgnoreCase);

    /// <summary>factory.ps1's [string] parameters. Any other token starting with '-' is a positional value (a rework note can).</summary>
    public static readonly IReadOnlySet<string> Values = new HashSet<string>(
        ["By", "Reason", "Detail", "File", "Job", "Idea", "Kind", "Agent", "Target", "Scaffold", "Base", "Session", "Product", "From", "Feature"], StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private init; } = "status";
    public IReadOnlyList<string> Positional { get; private init; } = [];

    public static CommandArgs Parse(IReadOnlyList<string> args)
    {
        var positional = new List<string>();
        var named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].Length >= 2 && args[i][0] == '-' ? args[i][1..] : "";
            if (Switches.Contains(name)) { named[name] = null; continue; }
            if (!Values.Contains(name)) { positional.Add(args[i]); continue; }
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

    /// <summary>Arg1..Arg3 as factory.ps1 sees them: a missing [string] parameter is "".</summary>
    public string Arg(int n) => n >= 1 && n <= Positional.Count ? Positional[n - 1] : "";

    /// <summary>A -Name value as factory.ps1's [string] parameter sees it: missing is "".</summary>
    public string Named(string name) => Get(name) ?? "";
}
