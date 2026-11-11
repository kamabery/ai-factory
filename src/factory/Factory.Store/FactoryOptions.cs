namespace Factory.Store;

/// <summary>Where the factory runs: bound from the environment by each host's composition root.</summary>
public sealed class FactoryOptions
{
    /// <summary>FACTORY_HOME: the state folder.</summary>
    public string? Home { get; set; }

    /// <summary>FACTORY_REPO: the main checkout, else the one git finds from <see cref="WorkingDirectory"/>.</summary>
    public string? Repo { get; set; }

    /// <summary>FACTORY_ENV_FILE: the .env file, else the main checkout's.</summary>
    public string? EnvFile { get; set; }

    /// <summary>FACTORY_WORK: where jobs' clones live, else &lt;temp&gt;/ai-factory.</summary>
    public string? Work { get; set; }

    /// <summary>FACTORY_TEMPLATES: the template-projects checkout, else the one beside this repo.</summary>
    public string? Templates { get; set; }

    /// <summary>factory/scripts: FACTORY_SCRIPTS (factory.ps1 sets it when it forwards), else found above the CLI.</summary>
    public string? Scripts { get; set; }

    /// <summary>FACTORY_GATE_CMDS: a JSON array of <c>{ name, cmd }</c> that replaces every spec's gate (tests use it).</summary>
    public string? GateCmds { get; set; }

    /// <summary>FACTORY_GATE_CHECK_WAIT_MINUTES: how long gate-check waits for another's lock (default 20).</summary>
    public string? GateCheckWaitMinutes { get; set; }

    /// <summary>FACTORY_MODE from the environment; the .env file's is read when this is unset.</summary>
    public string? Mode { get; set; }

    /// <summary>The system temp folder.</summary>
    public string TempPath { get; set; } = "";

    public string WorkingDirectory { get; set; } = "";
}
