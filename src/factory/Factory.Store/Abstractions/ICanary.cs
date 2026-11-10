using Factory.Core;

namespace Factory.Store.Abstractions;

/// <summary>The deploy canary (contract, "Canary"), as far as the queue needs it.</summary>
public interface ICanary
{
    /// <summary>What canary.json records for the current fingerprint (computed only when there is a record).</summary>
    CanaryState State();

    /// <summary>A SHA-256 over what the deploy path depends on (canary.ps1's Canary-Fingerprint).</summary>
    string Fingerprint();
}
