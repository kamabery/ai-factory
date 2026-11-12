namespace Factory.Runner;

/// <summary>What the runner reads besides the factory's own settings, bound by <see cref="RunnerComposition"/>.</summary>
public sealed class RunnerOptions
{
    /// <summary>FACTORY_CLAUDE_CMD: a pwsh command that replaces claude (tests spend no tokens).</summary>
    public string? ClaudeCmd { get; set; }
}
