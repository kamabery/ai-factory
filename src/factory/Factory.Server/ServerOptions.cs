namespace Factory.Server;

/// <summary>What the dashboard reads besides the factory's own settings: serve.ps1's parameters and its environment.</summary>
public sealed class ServerOptions
{
    /// <summary>--port: 0 (the default) takes a free loopback port, printed at start.</summary>
    public int Port { get; set; }

    /// <summary>--env-file, else the .env beside factory/: the file factory.ps1 is handed as FACTORY_ENV_FILE.</summary>
    public string EnvFile { get; set; } = "";

    /// <summary>factory/dashboard: the SPA's build, index.html and assets/ (src/factory/Factory.Dashboard builds it).</summary>
    public string DashboardDir { get; set; } = "";

    public int WatchdogSeconds { get; set; } = 10;

    public int PrSyncSeconds { get; set; } = 30;

    /// <summary>--parent-pid: serve.ps1's PID when it forwarded here; 0 when started directly (the AppHost, the tests).</summary>
    public int ParentPid { get; set; }

    /// <summary>FACTORY_STATE_POLL_SECONDS (default 5): how often, at most, the azure cache refreshes one thing.</summary>
    public double StatePollSeconds { get; set; } = 5;

    /// <summary>FACTORY_WORK, else &lt;system temp&gt;/ai-factory: where session clones live.</summary>
    public string WorkRoot { get; set; } = "";

    /// <summary>The process environment, read once: the run mode and Foundry settings come from here before .env.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; set; } = new Dictionary<string, string?>();
}
