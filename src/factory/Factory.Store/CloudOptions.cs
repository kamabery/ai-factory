namespace Factory.Store;

/// <summary>A deployed factory's settings (contract, "Cloud mode (deployed)"), from the environment only.</summary>
public sealed class CloudOptions
{
    /// <summary>FACTORY_RUNNER_JOB: the runner job's resource id; set, the runner starts as one execution of it.</summary>
    public string? RunnerJob { get; set; }
}
