namespace Factory.TestHost;

/// <summary>
/// A fake's arguments: its own <c>--name value</c> options before a <c>--</c> (the override's leading arguments, so a test
/// configures it without touching the environment), then the arguments of the program it stands in for. With no <c>--</c>,
/// every argument is the program's.
/// </summary>
public sealed record FakeArgs(IReadOnlyDictionary<string, string> Options, IReadOnlyList<string> Rest)
{
    public static FakeArgs Parse(IReadOnlyList<string> args)
    {
        var split = args.ToList().IndexOf("--");
        if (split < 0) return new(new Dictionary<string, string>(), args);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < split; i++)
            if (args[i].StartsWith("--", StringComparison.Ordinal))
                options[args[i][2..]] = i + 1 < split && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        return new(options, [.. args.Skip(split + 1)]);
    }

    /// <summary>The option, else the environment variable the PowerShell fake read, else null.</summary>
    public string? Get(string option, string? variable = null) =>
        Options.TryGetValue(option, out var v) ? v : variable is null ? null : Environment.GetEnvironmentVariable(variable) is { Length: > 0 } e ? e : null;

    public static string Stdin() => Console.IsInputRedirected ? Console.In.ReadToEnd() : "";
}
