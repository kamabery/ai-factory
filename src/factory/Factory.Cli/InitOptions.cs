namespace Factory.Cli;

/// <summary>factory init's flags: <c>--org</c>, <c>--foundry</c>, <c>--move-env-secrets</c>, <c>--no-open</c>.</summary>
public sealed record InitOptions(string? Org, string? Foundry, bool MoveEnvSecrets, bool NoOpen)
{
    public static InitOptions Parse(IReadOnlyList<string> args)
    {
        string? Value(string flag) => args.Select((a, i) => (a, i)).Where(x => x.a == flag && x.i + 1 < args.Count).Select(x => args[x.i + 1]).FirstOrDefault();
        var known = new[] { "--org", "--foundry", "--move-env-secrets", "--no-open" };
        var unknown = args.Where((a, i) => a.StartsWith("--", StringComparison.Ordinal) && !known.Contains(a)).ToList();
        if (unknown.Count > 0) throw new Core.FactoryException($"usage: factory init [--org <org>] [--foundry <resource>] [--move-env-secrets] [--no-open] (not {unknown[0]})");
        return new InitOptions(Value("--org"), Value("--foundry"), args.Contains("--move-env-secrets"), args.Contains("--no-open"));
    }
}
