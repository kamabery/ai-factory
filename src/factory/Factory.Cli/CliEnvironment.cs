namespace Factory.Cli;

/// <summary>What the process knows about where it runs, gathered once by <see cref="CliHost"/> for the composition root.</summary>
public sealed record CliEnvironment(
    IReadOnlyDictionary<string, string?> Variables,
    string WorkingDirectory,
    string NewLine,
    string LocalAppData,
    string TempPath,
    string BaseDirectory)
{
    public string? Get(string name) => Variables.GetValueOrDefault(name) is { Length: > 0 } v ? v : null;

    /// <summary>factory/scripts above <see cref="BaseDirectory"/> (the CLI's artifacts folder in this repo), or null.</summary>
    public string? FindScripts()
    {
        for (var d = new DirectoryInfo(BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "factory", "scripts");
            if (File.Exists(Path.Combine(p, "factory.ps1"))) return p;
        }
        return null;
    }
}
