using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>Only while HCP_ORG or HCP_TOKEN is set: HCP_TOKEN reads the account.</summary>
public sealed class HcpCheck(IDotEnv dotEnv, ISecrets secrets, IHcpClient hcp) : IDoctorCheck
{
    public string Name => "hcp";

    public DoctorResult? Check()
    {
        if (dotEnv.Get("HCP_ORG") is null && secrets.Get(SecretNames.HcpToken) is null) return null;
        try { return DoctorResult.Ok($"HCP Terraform as {hcp.Account()}"); }
        catch (FactoryException e) { return DoctorResult.Fail(e.Message, "set HCP_TOKEN in the secret store (factory init --move-env-secrets moves it from .env)"); }
    }
}
