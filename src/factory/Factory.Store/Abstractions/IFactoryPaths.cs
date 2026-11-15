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

    /// <summary>FACTORY_TEMPLATES, else template-projects beside this repo, or in the profile in a tool install.</summary>
    string TemplatesRoot { get; }

    /// <summary>True when the factory root is this host's own folder: the dotnet tool, not a checkout.</summary>
    bool IsToolInstall { get; }

    /// <summary>A tool install's profile (FACTORY_PROFILE, else &lt;user home&gt;/.ai-factory); null in a checkout install.</summary>
    string? Profile { get; }

    /// <summary>The .env.example a new .env starts from: the package's in a tool install, else beside the .env.</summary>
    string? EnvExample { get; }

    /// <summary>The folder put first on the agents' PATH so `factory` there is this install's.</summary>
    string CommandDir { get; }
}
