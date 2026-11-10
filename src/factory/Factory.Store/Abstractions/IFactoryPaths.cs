namespace Factory.Store.Abstractions;

public interface IFactoryPaths
{
    string Home { get; }
    string BoardPath { get; }
    string EventsPath { get; }

    /// <summary>FACTORY_ENV_FILE, else &lt;main checkout&gt;/.env; null outside a repository.</summary>
    string? EnvFile { get; }
}
