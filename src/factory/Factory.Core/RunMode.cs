namespace Factory.Core;

/// <summary>llm.ps1's run mode (contract, "Run mode and providers"): it decides the provider, never a job.</summary>
public static class RunMode
{
    /// <summary>Get-FactoryProvider: <c>local</c> (the default) is the subscription, <c>cloud</c> is Foundry; anything else refuses.</summary>
    public static string Provider(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return "subscription";
        var m = mode.Trim().ToLowerInvariant();
        return m switch
        {
            "local" => "subscription",
            "cloud" => "foundry",
            _ => throw new FactoryException($"FACTORY_MODE '{m}' must be local or cloud"),
        };
    }
}
