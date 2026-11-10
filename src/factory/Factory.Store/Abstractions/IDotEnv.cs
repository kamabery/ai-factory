namespace Factory.Store.Abstractions;

/// <summary>The .env file's values (FACTORY_ENV_FILE, else the main checkout's), read once.</summary>
public interface IDotEnv
{
    /// <summary>The value, or null when it is missing or empty.</summary>
    string? Get(string name);
}
