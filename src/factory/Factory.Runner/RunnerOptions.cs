namespace Factory.Runner;

/// <summary>What the runner reads besides the factory's own settings, bound by <see cref="RunnerComposition"/>.</summary>
public sealed class RunnerOptions
{
    /// <summary>FACTORY_CLAUDE_CMD: a pwsh command that replaces claude (tests spend no tokens).</summary>
    public string? ClaudeCmd { get; set; }

    /// <summary><c>Factory:Mode</c> from configuration: the run mode when neither the environment nor .env sets FACTORY_MODE.</summary>
    public string? ConfiguredMode { get; set; }

    /// <summary>The process environment, read once: FACTORY_MODE and the Foundry settings come from here before .env.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; set; } = new Dictionary<string, string?>();
}
