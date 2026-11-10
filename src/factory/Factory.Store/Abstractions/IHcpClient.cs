namespace Factory.Store.Abstractions;

/// <summary>HCP Terraform, as far as the port needs it so far (hcp.ps1). Spec 09 ports the rest.</summary>
public interface IHcpClient
{
    /// <summary>The Azure variable set's variables (HCP_AZURE_VARSET); none when no varset is set.</summary>
    IReadOnlyList<HcpVar> VarsetVars();
}
