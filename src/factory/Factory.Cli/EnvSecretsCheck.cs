using Factory.Cli.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>.env holds none of the secrets: they belong in the store (a warn, since a secret in .env still works).</summary>
public sealed class EnvSecretsCheck(ISecrets secrets) : IDoctorCheck
{
    public string Name => "env-secrets";

    public DoctorResult Check()
    {
        var left = secrets.InEnvFile();
        return left.Count == 0
            ? DoctorResult.Ok("no secrets in .env")
            : DoctorResult.Warn($"still in .env: {string.Join(", ", left)}", "factory init --move-env-secrets");
    }
}
