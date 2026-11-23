namespace Factory.Cli.Abstractions;

/// <summary>
/// The shared preview platform's Postgres server, the one platform resource with a real idle cost (contract, "The
/// platform", spec 24). Every member refuses with deploys off or without FACTORY_PLATFORM_SERVER_ID.
/// </summary>
public interface IPlatform
{
    /// <summary>Whether deploys are on (HCP_ORG), so the platform exists to wake or put to sleep.</summary>
    bool Enabled { get; }

    /// <summary><c>awake</c>, <c>asleep</c>, <c>starting</c>, <c>stopping</c>, or the server's own state in lower case.</summary>
    string Status();

    /// <summary>Starts the server unless it is awake, and returns once it is <c>Ready</c>.</summary>
    string Start();

    /// <summary>Stops the server without waiting. The caller checks it is not asleep and applies the guard first.</summary>
    string Sleep();
}
