namespace Factory.Store.Abstractions;

/// <summary>
/// doctor's cloud check: one request, max_tokens 1, to a deployment of the Foundry resource, signed in through Entra
/// (az, scope https://ai.azure.com). Null when it answered; else why not.
/// </summary>
public interface IFoundryProbe
{
    string? Ping(string resource, string deployment);
}
