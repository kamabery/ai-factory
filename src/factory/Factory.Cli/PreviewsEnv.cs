namespace Factory.Cli;

/// <summary>The settings <c>factory previews-env</c> writes, in the order it prints them (contract, "Deploy").</summary>
public static class PreviewsEnv
{
    public static readonly IReadOnlyList<string> Settings =
    [
        "AZURE_GH_CLIENT_ID", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "HCP_AZURE_VARSET",
        "FACTORY_ENTRA_TENANT_ID", "FACTORY_ENTRA_SUBDOMAIN", "FACTORY_ENTRA_USER_FLOW_ID",
    ];

    /// <summary>The variables the preview setup puts on HCP_AZURE_VARSET, which every preview's Terraform reads.</summary>
    public static readonly IReadOnlyList<string> VarsetKeys =
        ["TFC_AZURE_PROVIDER_AUTH", "TFC_AZURE_RUN_CLIENT_ID", "ARM_SUBSCRIPTION_ID", "ARM_TENANT_ID", "deployer_object_id", "platform"];

    /// <summary>Where the setup is, relative to the factory root.</summary>
    public const string InfraDir = "factory/infra/previews";
}
