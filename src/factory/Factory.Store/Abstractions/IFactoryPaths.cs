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

    /// <summary>&lt;work&gt;/jobs/&lt;id&gt;: the job's clone of its target, deleted at done or discarded.</summary>
    string WorkDir(string id);

    /// <summary>factory/scripts, where factory.ps1 and the deploy scripts the canary fingerprints live.</summary>
    string ScriptsDir { get; }

    /// <summary>FACTORY_TEMPLATES, else template-projects beside this repo (factory.ps1's TemplatesRoot).</summary>
    string TemplatesRoot { get; }
}
