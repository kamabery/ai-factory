using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>A secret from the environment, else the store, else .env.</summary>
public sealed class Secrets(IOptions<SecretOptions> options, ISecretStore store, IDotEnv dotEnv) : ISecrets
{
    public string? Get(string name) =>
        NonEmpty(options.Value.Environment.GetValueOrDefault(name)) ?? NonEmpty(store.Get(name)) ?? NonEmpty(dotEnv.Get(name));

    public IReadOnlyList<string> InEnvFile() => [.. SecretNames.All.Where(n => NonEmpty(dotEnv.Get(n)) is not null)];

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
