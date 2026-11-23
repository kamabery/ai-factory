using Factory.Core;

namespace Factory.Store.Abstractions;

/// <summary>The deploy canary (contract, "Canary"), its fingerprint, state and settings.</summary>
public interface ICanary
{
    /// <summary>What canary.json records for the current fingerprint (computed only when there is a record).</summary>
    CanaryState State();

    /// <summary>A SHA-256 over what the deploy path depends on (canary.ps1's Canary-Fingerprint).</summary>
    string Fingerprint();

    /// <summary>A SHA-256 of each of the fingerprint's four groups of lines: deploy, templates, varset, settings (contract, "Why it is due").</summary>
    IReadOnlyDictionary<string, string> Inputs();

    /// <summary>The factory package version running, as canary.json's <c>version</c> records it.</summary>
    string Version { get; }

    /// <summary>The canary's repo: FACTORY_CANARY_REPO, else &lt;GITHUB_OWNER&gt;/factory-canary; refuses with neither.</summary>
    string Repo();

    /// <summary>
    /// The canary's scaffold: FACTORY_CANARY_SCAFFOLD, else a react front end with sign-in, plus, with auth, the External
    /// ID ids from .env under <c>entra</c>; refuses invalid options, or auth without the ids (canary 15).
    /// </summary>
    System.Text.Json.Nodes.JsonObject Options();
}
