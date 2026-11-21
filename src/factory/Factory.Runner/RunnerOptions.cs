namespace Factory.Runner;

/// <summary>What the runner reads besides the factory's own settings, bound by <see cref="RunnerComposition"/>.</summary>
public sealed class RunnerOptions
{
    /// <summary>FACTORY_CLAUDE_CMD: a program and its leading arguments that replace claude (tests spend no tokens).</summary>
    public string? ClaudeCmd { get; set; }

    /// <summary>--one: take at most one job, start the runner again while the queue is not empty, and exit (a job execution).</summary>
    public bool One { get; set; }

    /// <summary>FACTORY_CONTAINER (set by factory container): every runner.log line is also written to stdout.</summary>
    public bool Echo { get; set; }

    /// <summary>
    /// Off Windows, with DOTNET_ROOT unset: the .NET this runner runs on, given to claude so the <c>factory</c> launcher
    /// it finds on PATH can find a user-local install. Null otherwise.
    /// </summary>
    public string? DotnetRoot { get; set; }
}
