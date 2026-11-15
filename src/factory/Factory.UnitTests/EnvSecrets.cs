using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>Secrets that come only from the given .env (an empty environment and store), as before the store existed.</summary>
public static class EnvSecrets
{
    public static ISecrets Over(IDotEnv dotEnv)
    {
        var store = Substitute.For<ISecretStore>();
        store.Get(Arg.Any<string>()).Returns((string?)null);
        return new Secrets(Options.Create(new SecretOptions()), store, dotEnv);
    }
}
