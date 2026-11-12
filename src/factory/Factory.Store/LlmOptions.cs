namespace Factory.Store;

/// <summary>What <see cref="LlmProvider"/> reads besides .env: the configured mode and the process environment.</summary>
public sealed class LlmOptions
{
    /// <summary><c>Factory:Mode</c> from configuration: the run mode when neither the environment nor .env sets FACTORY_MODE.</summary>
    public string? ConfiguredMode { get; set; }

    /// <summary>The process environment, read once: FACTORY_MODE and the Foundry settings come from here before .env.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; set; } = new Dictionary<string, string?>();
}
