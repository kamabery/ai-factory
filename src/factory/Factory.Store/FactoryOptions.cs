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

    /// <summary>factory/scripts: FACTORY_SCRIPTS, else found above this host's build folder.</summary>
    public string? Scripts { get; set; }

    /// <summary>FACTORY_CLI: the CLI's factory.dll, else the one built beside this host.</summary>
    public string? Cli { get; set; }

    /// <summary>FACTORY_RUNNER: the runner's Factory.Runner.dll, else the one built beside this host.</summary>
    public string? Runner { get; set; }

    /// <summary>FACTORY_SERVER: the dashboard server's Factory.Server.dll, else the one built beside this host.</summary>
    public string? Server { get; set; }

    /// <summary>This host's build folder (artifacts/bin/&lt;project&gt;/&lt;configuration&gt;/).</summary>
    public string BaseDirectory { get; set; } = "";

    /// <summary>FACTORY_GATE_CMDS: a JSON array of <c>{ name, cmd }</c> that replaces every spec's gate (tests use it).</summary>
    public string? GateCmds { get; set; }

    /// <summary>FACTORY_GATE_SHELL: the shell a gate line left from before shell-neutral gates runs in (a program on PATH, or a path).</summary>
    public string? GateShell { get; set; }

    /// <summary>FACTORY_GATE_CHECK_WAIT_MINUTES: how long gate-check waits for another's lock (default 20).</summary>
    public string? GateCheckWaitMinutes { get; set; }

    /// <summary>FACTORY_MODE from the environment; the .env file's is read when this is unset.</summary>
    public string? Mode { get; set; }

    /// <summary>FACTORY_PROFILE: a tool install's profile folder, else &lt;user home&gt;/.ai-factory.</summary>
    public string? Profile { get; set; }

    /// <summary>The user's home folder (a tool install's default profile sits in it).</summary>
    public string UserHome { get; set; } = "";

    /// <summary>DOTNET_CLI_HOME: where dotnet keeps its global tools (&lt;it&gt;/.dotnet/tools), else the user home.</summary>
    public string? DotnetCliHome { get; set; }

    /// <summary>The system temp folder.</summary>
    public string TempPath { get; set; } = "";

    public string WorkingDirectory { get; set; } = "";
}
