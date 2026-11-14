namespace Factory.Runner;

/// <summary>What the runner reads besides the factory's own settings, bound by <see cref="RunnerComposition"/>.</summary>
public sealed class RunnerOptions
{
    /// <summary>FACTORY_CLAUDE_CMD: a pwsh command that replaces claude (tests spend no tokens).</summary>
    public string? ClaudeCmd { get; set; }

    /// <summary>
    /// Off Windows, with DOTNET_ROOT unset: the .NET this runner runs on, given to claude so the <c>factory</c> launcher
    /// it finds on PATH can find a user-local install. Null otherwise.
    /// </summary>
    public string? DotnetRoot { get; set; }
}
