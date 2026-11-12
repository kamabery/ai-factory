namespace Factory.Server.Abstractions;

/// <summary>The .env values the dashboard shows or checks, read afresh on each call as serve.ps1's Get-EnvValue did.</summary>
public interface IServerSettings
{
    /// <summary>One .env value, or null when it is missing or empty. Never a key the dashboard returns.</summary>
    string? Env(string name);

    /// <summary>local or cloud (FACTORY_MODE, environment then .env, default local); null when FACTORY_MODE is invalid.</summary>
    string? Mode { get; }

    /// <summary>Why idea sessions cannot start in this mode, or null when the mode's credential is configured.</summary>
    string? IdeaBlocker { get; }

    /// <summary>FACTORY_IDEA_MODEL, else claude-opus-5-5.</summary>
    string IdeaModel { get; }
}
