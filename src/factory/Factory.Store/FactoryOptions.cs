namespace Factory.Store;

/// <summary>Where the factory runs: bound from the environment by each host's composition root.</summary>
public sealed class FactoryOptions
{
    /// <summary>FACTORY_HOME: the state folder.</summary>
    public string? Home { get; set; }

    /// <summary>FACTORY_REPO: the main checkout, else the one git finds from <see cref="WorkingDirectory"/>.</summary>
    public string? Repo { get; set; }

    public string WorkingDirectory { get; set; } = "";
}
