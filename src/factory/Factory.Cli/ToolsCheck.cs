using Factory.Cli.Abstractions;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>git, dotnet and claude run; az too in cloud mode, terraform and gh while HCP_ORG is set; git has a user.name and user.email.</summary>
public sealed class ToolsCheck(IProcessRunner processes, ILlmProvider llm, IDotEnv dotEnv) : IDoctorCheck
{
    public string Name => "tools";

    /// <summary>The tools this machine needs, each with the program to start and the arguments that print its version.</summary>
    public IReadOnlyList<(string Tool, string Program, string[] Args)> Needed(bool foundry)
    {
        List<(string, string, string[])> tools = [("git", "git", ["--version"]), ("dotnet", "dotnet", ["--version"]), ("claude", ClaudeCli.Executable(), ["--version"])];
        if (foundry) tools.Add(("az", StateToken.AzProgram, ["version", "--output", "none"]));
        if (dotEnv.Get("HCP_ORG") is not null)
        {
            tools.Add(("terraform", "terraform", ["version"]));
            tools.Add(("gh", "gh", ["--version"]));
        }
        return tools;
    }

    /// <summary>The tools that do not run.</summary>
    public IReadOnlyList<string> Missing(bool foundry) => [.. Needed(foundry).Where(t => Version(t.Program, t.Args) is null).Select(t => t.Tool)];

    public DoctorResult Check()
    {
        bool foundry;
        try { foundry = llm.ChildEnvironment().GetValueOrDefault("CLAUDE_CODE_USE_FOUNDRY") == "1"; }
        catch (Core.FactoryException) { foundry = true; }
        var found = Needed(foundry).Select(t => (t.Tool, Version: Version(t.Program, t.Args))).ToList();
        var missing = found.Where(f => f.Version is null).Select(f => f.Tool).ToList();
        if (missing.Count > 0)
            return DoctorResult.Fail($"not found: {string.Join(", ", missing)}",
                string.Join("; ", missing.Select(m => InstallHints.For(m, OperatingSystem.IsWindows()))));
        // The builder commits in the job's clone; a fresh machine's git has no identity to commit as.
        if (new[] { "user.name", "user.email" }.FirstOrDefault(k => string.IsNullOrWhiteSpace(Version("git", ["config", k]))) is { } unset)
            return DoctorResult.Fail($"git has no {unset}; the builder's commits need one",
                "git config --global user.name \"<your name>\"; git config --global user.email <your email>");
        return DoctorResult.Ok(string.Join(", ", found.Select(f => f.Version!.Length > 0 ? $"{f.Tool} {f.Version}" : f.Tool)));
    }

    // The first line of its version output (empty for az, which prints nothing with --output none); null when it does not run.
    private string? Version(string program, string[] args)
    {
        try
        {
            var r = processes.Run(program, args, "");
            if (r.ExitCode != 0) return null;
            var line = r.StdOut.Split('\n').FirstOrDefault()?.Trim() ?? "";
            return line.Replace("git version ", "", StringComparison.Ordinal).Replace(" (Claude Code)", "", StringComparison.Ordinal).Replace("Terraform v", "", StringComparison.Ordinal).Replace("gh version ", "", StringComparison.Ordinal);
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
