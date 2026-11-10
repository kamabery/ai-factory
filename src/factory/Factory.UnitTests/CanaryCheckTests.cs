using System.Security.Cryptography;
using System.Text;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class CanaryCheckTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "canary-fx");
    private static readonly string Scripts = Path.Combine(Root, "repo", "factory", "scripts");
    private static readonly string Templates = Path.Combine(Root, "templates");
    private static readonly string Home = Path.Combine(Root, "home");
    private readonly MemoryFiles _files = new();
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();
    private readonly IHcpClient _hcp = Substitute.For<IHcpClient>();
    private readonly CanaryCheck _sut;

    public CanaryCheckTests()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.ScriptsDir.Returns(Scripts);
        paths.TemplatesRoot.Returns(Templates);
        foreach (var f in new[] { "deploy.ps1", "hcp.ps1", "github.ps1" }) _files.Put(Path.Combine(Scripts, f), f);
        _files.Put(Path.Combine(Templates, "IaC", "main.tf"), "m");
        _files.Put(Path.Combine(Templates, "IaC", "envs", "a.tfvars"), "a");
        _files.Put(Path.Combine(Templates, "IaC", ".terraform", "x"), "skipped");
        _files.Put(Path.Combine(Templates, "base-project", ".github", "workflows", "deploy.yml"), "d");
        _files.Put(Path.Combine(Templates, "artifacts", "nupkg", "T.1.0.0.nupkg"), "n");
        _files.Put(Path.Combine(Templates, "artifacts", "nupkg", "sub", "skipped.nupkg"), "x");
        _env.Get(Arg.Any<string>()).Returns((string?)null);
        _env.Get("GITHUB_OWNER").Returns("me");
        _env.Get("FACTORY_ENTRA_TENANT_ID").Returns("t");
        _env.Get("FACTORY_ENTRA_SUBDOMAIN").Returns("s");
        _env.Get("FACTORY_ENTRA_USER_FLOW_ID").Returns("f");
        _hcp.VarsetVars().Returns([new HcpVar("terraform", "b", "2", false), new HcpVar("env", "z", null, false), new HcpVar("terraform", "a", "secret", true)]);
        _sut = new CanaryCheck(paths, _files, _env, _hcp);
    }

    private static string H(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Fact]
    public void Fingerprints_the_deploy_scripts_templates_varset_and_canary_settings_as_canary_ps1_does()
    {
        var lines = string.Join("\n",
            $"factory/deploy.ps1 {H("deploy.ps1")}", $"factory/hcp.ps1 {H("hcp.ps1")}", $"factory/github.ps1 {H("github.ps1")}",
            $"artifacts/nupkg/T.1.0.0.nupkg {H("n")}", $"base-project/.github/workflows/deploy.yml {H("d")}",
            $"IaC/envs/a.tfvars {H("a")}", $"IaC/main.tf {H("m")}",
            "var env z ", "var terraform a <sensitive>", "var terraform b 2",
            """canary me/factory-canary {"name":"canary","resource":"Item","db":"postgres","ui":"react","events":false,"auth":true,"entra":{"entra_external_tenant_id":"t","entra_external_subdomain":"s","entra_external_user_flow_id":"f"}}""");

        Assert.Equal("sha256:" + H(lines).ToLowerInvariant(), _sut.Fingerprint());
    }

    [Fact]
    public void State_reads_canary_json_and_computes_the_fingerprint_only_when_there_is_one()
    {
        Assert.Equal(CanaryState.None, _sut.State());
        _hcp.DidNotReceive().VarsetVars();

        _files.Put(Path.Combine(Home, "canary.json"), $$"""{"fingerprint":"{{_sut.Fingerprint()}}","status":"pass"}""");
        Assert.Equal(CanaryState.Pass, _sut.State());
        _files.Put(Path.Combine(Home, "canary.json"), """{"fingerprint":"sha256:old","status":"pass"}""");
        Assert.Equal(CanaryState.None, _sut.State());
    }

    [Fact]
    public void Refuses_without_a_canary_repo_a_bad_scaffold_or_sign_in_ids()
    {
        _env.Get("GITHUB_OWNER").Returns((string?)null);
        Assert.Equal("canary refused: set FACTORY_CANARY_REPO (or GITHUB_OWNER) in .env", Assert.Throws<FactoryException>(_sut.Fingerprint).Message);

        _env.Get("FACTORY_CANARY_REPO").Returns("o/c");
        _env.Get("FACTORY_ENTRA_SUBDOMAIN").Returns((string?)null);
        Assert.StartsWith("canary refused: the canary scaffold has auth, and .env lacks", Assert.Throws<FactoryException>(_sut.Fingerprint).Message);

        _env.Get("FACTORY_CANARY_SCAFFOLD").Returns("""{"name":"X"}""");
        Assert.StartsWith("canary refused: FACTORY_CANARY_SCAFFOLD: scaffold name 'X'", Assert.Throws<FactoryException>(_sut.Fingerprint).Message);

        _env.Get("FACTORY_CANARY_SCAFFOLD").Returns("""{"name":"x","resource":"X","db":"sqlite","ui":"none"}""");
        Assert.StartsWith("sha256:", _sut.Fingerprint());
    }
}
