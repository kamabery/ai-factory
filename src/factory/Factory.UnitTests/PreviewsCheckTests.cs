using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

/// <summary>doctor's <c>previews</c> check (contract, "Secrets, init and doctor"): the setup's settings and variable set.</summary>
public class PreviewsCheckTests
{
    private static readonly string[] Settings =
    [
        "AZURE_GH_CLIENT_ID", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID", "HCP_AZURE_VARSET", "FACTORY_PLATFORM_SERVER_ID",
        "FACTORY_ENTRA_TENANT_ID", "FACTORY_ENTRA_SUBDOMAIN", "FACTORY_ENTRA_USER_FLOW_ID",
    ];

    private const string Fix = "terraform apply in factory/infra/previews, then factory previews-env (docs/DEPLOY.md)";
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();
    private readonly IHcpClient _hcp = Substitute.For<IHcpClient>();

    public PreviewsCheckTests()
    {
        _env.Get(Arg.Any<string>()).Returns(c => c.Arg<string>() + "-value");
        _hcp.VarsetVars().Returns([.. new[] { "TFC_AZURE_PROVIDER_AUTH", "TFC_AZURE_RUN_CLIENT_ID", "ARM_SUBSCRIPTION_ID", "ARM_TENANT_ID" }
            .Select(k => new HcpVar("env", k, "v", false)), new HcpVar("terraform", "deployer_object_id", "d", false),
            new HcpVar("terraform", "platform", "{}", false), new HcpVar("env", "EXTRA", "x", false)]);
    }

    private DoctorResult? Check() => new PreviewsCheck(_env, _hcp).Check();

    [Fact]
    public void Is_named_previews_and_skipped_while_deploys_are_off()
    {
        Assert.Equal("previews", new PreviewsCheck(_env, _hcp).Name);
        _env.Get("HCP_ORG").Returns((string?)null);

        Assert.Null(Check());
        _hcp.DidNotReceive().VarsetVars();
    }

    [Fact]
    public void Passes_with_every_setting_and_variable()
    {
        Assert.Equal(DoctorResult.Ok("variable set HCP_AZURE_VARSET-value: 6 variables; .env has the 8 settings"), Check());
    }

    [Fact]
    public void Fails_naming_the_missing_settings_and_variables()
    {
        _env.Get("AZURE_TENANT_ID").Returns((string?)null);
        _env.Get("FACTORY_ENTRA_USER_FLOW_ID").Returns((string?)null);
        _hcp.VarsetVars().Returns([new HcpVar("env", "ARM_TENANT_ID", "t", false)]);

        Assert.Equal(DoctorResult.Fail(
            "missing from .env: AZURE_TENANT_ID, FACTORY_ENTRA_USER_FLOW_ID; missing from the variable set: TFC_AZURE_PROVIDER_AUTH, TFC_AZURE_RUN_CLIENT_ID, ARM_SUBSCRIPTION_ID, deployer_object_id, platform",
            Fix), Check());
    }

    [Fact]
    public void With_no_varset_setting_it_does_not_call_HCP()
    {
        _env.Get("HCP_AZURE_VARSET").Returns((string?)null);

        Assert.Equal(DoctorResult.Fail("missing from .env: HCP_AZURE_VARSET", Fix), Check());
        _hcp.DidNotReceive().VarsetVars();
    }

    [Fact]
    public void Fails_with_HCPs_refusal()
    {
        _hcp.VarsetVars().Throws(new FactoryException("HCP 404 on GET /varsets/x/relationships/vars: not found"));

        Assert.Equal(DoctorResult.Fail("HCP 404 on GET /varsets/x/relationships/vars: not found", Fix), Check());
    }

    [Fact]
    public void Lists_each_setting_the_contract_names()
    {
        Assert.Equal(Settings, PreviewsEnv.Settings);
    }
}
