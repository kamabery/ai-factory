namespace Factory.Store.Abstractions;

/// <summary>A secret by name: the process environment, else the secret store, else .env (which doctor warns about).</summary>
public interface ISecrets
{
    /// <summary>The value, or null when none of the three has it.</summary>
    string? Get(string name);

    /// <summary>The secrets .env still holds, in <see cref="Store.SecretNames.All"/> order.</summary>
    IReadOnlyList<string> InEnvFile();
}
