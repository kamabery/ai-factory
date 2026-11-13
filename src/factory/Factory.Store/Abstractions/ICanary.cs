using Factory.Core;

namespace Factory.Store.Abstractions;

/// <summary>The deploy canary (contract, "Canary"), its fingerprint, state and settings.</summary>
public interface ICanary
{
    /// <summary>What canary.json records for the current fingerprint (computed only when there is a record).</summary>
    CanaryState State();

    /// <summary>A SHA-256 over what the deploy path depends on (canary.ps1's Canary-Fingerprint).</summary>
    string Fingerprint();

    /// <summary>The canary's repo: FACTORY_CANARY_REPO, else &lt;GITHUB_OWNER&gt;/factory-canary; refuses with neither.</summary>
    string Repo();

    /// <summary>
    /// The canary's scaffold: FACTORY_CANARY_SCAFFOLD, else a react front end with sign-in, plus, with auth, the External
    /// ID ids from .env under <c>entra</c>; refuses invalid options, or auth without the ids (canary 15).
    /// </summary>
    System.Text.Json.Nodes.JsonObject Options();
}
