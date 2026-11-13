namespace Factory.Store.Abstractions;

public interface IFactoryPaths
{
    string Home { get; }
    string BoardPath { get; }
    string EventsPath { get; }

    /// <summary>FACTORY_ENV_FILE, else &lt;main checkout&gt;/.env; null outside a repository.</summary>
    string? EnvFile { get; }

    /// <summary>&lt;home&gt;/jobs/&lt;id&gt;: the job's spec, rounds and logs.</summary>
    string JobDir(string id);

    /// <summary>FACTORY_WORK, else &lt;system temp&gt;/ai-factory: where jobs' clones (and a new repo's seed) live.</summary>
    string WorkRoot { get; }

    /// <summary>&lt;work&gt;/jobs/&lt;id&gt;: the job's clone of its target, deleted at done or discarded.</summary>
    string WorkDir(string id);

    /// <summary>factory/scripts in the factory's checkout, the anchor for the plugin, the templates and the deploy code.</summary>
    string ScriptsDir { get; }

    /// <summary>The CLI, run as <c>dotnet &lt;CliDll&gt;</c>: FACTORY_CLI, else factory.dll built beside this host.</summary>
    string CliDll { get; }

    /// <summary>The runner: FACTORY_RUNNER, else Factory.Runner.dll built beside this host.</summary>
    string RunnerDll { get; }

    /// <summary>The dashboard server: FACTORY_SERVER, else Factory.Server.dll built beside this host.</summary>
    string ServerDll { get; }

    /// <summary>FACTORY_TEMPLATES, else template-projects beside this repo (factory.ps1's TemplatesRoot).</summary>
    string TemplatesRoot { get; }
}
