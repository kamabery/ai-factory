using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>Only while HCP_ORG is set: .env has the previews-env settings, and HCP_AZURE_VARSET holds what previews read.</summary>
public sealed class PreviewsCheck(IDotEnv dotEnv, IHcpClient hcp) : IDoctorCheck
{
    private const string Fix = $"terraform apply in {PreviewsEnv.InfraDir}, then factory previews-env (docs/DEPLOY.md)";

    public string Name => "previews";

    public DoctorResult? Check()
    {
        if (dotEnv.Get("HCP_ORG") is null) return null;
        var settings = PreviewsEnv.Settings.Where(s => dotEnv.Get(s) is null).ToList();
        if (dotEnv.Get("HCP_AZURE_VARSET") is not { } varset) return DoctorResult.Fail($"missing from .env: {string.Join(", ", settings)}", Fix);
        List<string> vars;
        try
        {
            var have = hcp.VarsetVars().Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
            vars = [.. PreviewsEnv.VarsetKeys.Where(k => !have.Contains(k))];
        }
        catch (FactoryException e) { return DoctorResult.Fail(e.Message, Fix); }
        List<string> missing = [];
        if (settings.Count > 0) missing.Add($"missing from .env: {string.Join(", ", settings)}");
        if (vars.Count > 0) missing.Add($"missing from the variable set: {string.Join(", ", vars)}");
        return missing.Count > 0
            ? DoctorResult.Fail(string.Join("; ", missing), Fix)
            : DoctorResult.Ok($"variable set {varset}: {PreviewsEnv.VarsetKeys.Count} variables; .env has the {PreviewsEnv.Settings.Count} settings");
    }
}
