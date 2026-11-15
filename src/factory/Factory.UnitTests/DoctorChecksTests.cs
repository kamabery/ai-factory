using System.ComponentModel;
using System.Security.Cryptography;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

/// <summary>Each of factory doctor's checks (contract, "Secrets, init and doctor"), on substitutes.</summary>
public class DoctorChecksTests
{
    private static readonly IReadOnlyDictionary<string, string?> Local = new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = null };
    private readonly IProcessRunner _proc = Substitute.For<IProcessRunner>();
    private readonly ILlmProvider _llm = Substitute.For<ILlmProvider>();
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();

    public DoctorChecksTests()
    {
        _env.Get(Arg.Any<string>()).Returns((string?)null);
        _llm.ChildEnvironment().Returns(Local);
        _proc.Run(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(c => new ProcessResult(0, c.ArgAt<string>(0) switch
        {
            "git" => "git version 2.47.1\n", "dotnet" => "10.0.402\n", _ => "2.1.325 (Claude Code)\n",
        }, ""));
    }

    // --- tools ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Tools_passes_with_each_version()
    {
        Assert.Equal(DoctorResult.Ok("git 2.47.1, dotnet 10.0.402, claude 2.1.325"), new ToolsCheck(_proc, _llm, _env).Check());
    }

    [Fact]
    public void Tools_names_what_is_missing_and_how_to_install_it_and_adds_az_and_terraform_when_needed()
    {
        _llm.ChildEnvironment().Returns(new Dictionary<string, string?> { ["CLAUDE_CODE_USE_FOUNDRY"] = "1" });
        _env.Get("HCP_ORG").Returns("org");
        _proc.Run("git", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Throws(new Win32Exception(2));
        _proc.Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(1, "", "no"));

        var r = new ToolsCheck(_proc, _llm, _env).Check();

        Assert.Equal((DoctorStatus.Fail, "not found: git, az"), (r.Status, r.Detail));
        Assert.Equal($"{InstallHints.For("git", OperatingSystem.IsWindows())}; {InstallHints.For("az", OperatingSystem.IsWindows())}", r.Fix);
        _proc.Received(1).Run("terraform", Arg.Any<IReadOnlyList<string>>(), "");
    }

    // --- secret-store --------------------------------------------------------------------------------------------

    [Fact]
    public void The_store_passes_when_a_probe_round_trips_and_fails_with_its_refusal()
    {
        var mem = new Dictionary<string, string>();
        var store = Substitute.For<ISecretStore>();
        store.Name.Returns("the vault");
        store.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>())).Do(c => mem[c.ArgAt<string>(0)] = c.ArgAt<string>(1));
        store.Get(Arg.Any<string>()).Returns(c => mem.GetValueOrDefault(c.Arg<string>()));
        store.When(s => s.Delete(Arg.Any<string>())).Do(c => mem.Remove(c.Arg<string>()));

        Assert.Equal(DoctorResult.Ok("the vault"), new SecretStoreCheck(store).Check());

        store.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>())).Throw(new FactoryException("secret store: no service"));
        var r = new SecretStoreCheck(store).Check();
        Assert.Equal((DoctorStatus.Fail, "secret store: no service"), (r.Status, r.Detail));
    }

    [Fact]
    public void The_store_fails_when_it_gives_back_something_else()
    {
        var store = Substitute.For<ISecretStore>();
        store.Name.Returns("the vault");
        store.Get(Arg.Any<string>()).Returns("other");

        Assert.Equal(DoctorStatus.Fail, new SecretStoreCheck(store).Check().Status);
    }

    // --- env-secrets ---------------------------------------------------------------------------------------------

    [Fact]
    public void Secrets_left_in_env_are_a_warn_with_the_move()
    {
        var secrets = Substitute.For<ISecrets>();
        secrets.InEnvFile().Returns([]);
        Assert.Equal(DoctorResult.Ok("no secrets in .env"), new EnvSecretsCheck(secrets).Check());

        secrets.InEnvFile().Returns(["GITHUB_TOKEN", "HCP_TOKEN"]);
        Assert.Equal(DoctorResult.Warn("still in .env: GITHUB_TOKEN, HCP_TOKEN", "factory init --move-env-secrets"), new EnvSecretsCheck(secrets).Check());
    }

    // --- llm -----------------------------------------------------------------------------------------------------

    private LlmCheck Llm(IFoundryProbe? probe = null) => new(_llm, _proc, probe ?? Substitute.For<IFoundryProbe>());

    private void AuthStatus(string json) =>
        _proc.Run(ClaudeCli.Executable(), Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "auth", "status" })), "", null, Local)
            .Returns(new ProcessResult(0, json, ""));

    [Fact]
    public void Local_needs_claude_logged_in_to_a_subscription_as_the_runner_starts_it()
    {
        AuthStatus("""{"loggedIn":true,"authMethod":"claude.ai"}""");
        Assert.Equal(DoctorResult.Ok("local mode: claude is logged in to a claude.ai subscription"), Llm().Check());

        AuthStatus("""{"loggedIn":true,"authMethod":"api_key"}""");
        Assert.Equal(("local mode: claude is logged in with api_key, not a claude.ai subscription", "run claude, then /login, and choose your claude.ai account"),
            (Llm().Check().Detail, Llm().Check().Fix));

        AuthStatus("""{"loggedIn":false}""");
        Assert.Equal("local mode: claude is not logged in", Llm().Check().Detail);
    }

    [Fact]
    public void Cloud_pings_each_pinned_deployment_once_and_names_the_ones_that_fail()
    {
        _llm.ChildEnvironment().Returns(new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_USE_FOUNDRY"] = "1", ["ANTHROPIC_FOUNDRY_RESOURCE"] = "fdy",
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "opus", ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = "sonnet", ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "sonnet",
        });
        var probe = Substitute.For<IFoundryProbe>();
        probe.Ping("fdy", Arg.Any<string>()).Returns((string?)null);

        Assert.Equal(DoctorResult.Ok("cloud mode, fdy: opus, sonnet answered"), Llm(probe).Check());

        probe.Ping("fdy", "sonnet").Returns("404 DeploymentNotFound");
        Assert.Equal("cloud mode, fdy: sonnet: 404 DeploymentNotFound", Llm(probe).Check().Detail);
    }

    [Fact]
    public void Cloud_with_no_resource_fails_and_says_how_to_choose_one()
    {
        _llm.ChildEnvironment().Throws(new FactoryException("no FACTORY_FOUNDRY_RESOURCE"));

        Assert.Equal(DoctorResult.Fail("cloud mode: no FACTORY_FOUNDRY_RESOURCE", "factory init --foundry <resource>"), Llm().Check());
    }

    // --- hcp -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Hcp_is_skipped_without_HCP_settings_and_reads_the_account_with_them()
    {
        var secrets = Substitute.For<ISecrets>();
        secrets.Get(Arg.Any<string>()).Returns((string?)null);
        var hcp = Substitute.For<IHcpClient>();
        Assert.Null(new HcpCheck(_env, secrets, hcp).Check());

        secrets.Get("HCP_TOKEN").Returns("t");
        hcp.Account().Returns("kamabery");
        Assert.Equal(DoctorResult.Ok("HCP Terraform as kamabery"), new HcpCheck(_env, secrets, hcp).Check());

        hcp.Account().Throws(new FactoryException("HCP GET /account/details: 401"));
        Assert.Equal(DoctorStatus.Fail, new HcpCheck(_env, secrets, hcp).Check()!.Status);
    }

    // --- the report ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_report_has_a_line_per_check_a_fix_under_warns_and_fails_and_fails_only_on_a_fail()
    {
        IDoctorCheck C(string name, DoctorResult? r) { var c = Substitute.For<IDoctorCheck>(); c.Name.Returns(name); c.Check().Returns(r); return c; }
        var output = new StringWriter { NewLine = "\n" };

        var ok = new Doctor([C("tools", DoctorResult.Ok("git 2")), C("env-secrets", DoctorResult.Warn("still in .env: X", "move it")), C("hcp", null)]).Run(output);

        Assert.True(ok);
        Assert.Equal("ok    tools: git 2\nwarn  env-secrets: still in .env: X\n      fix: move it\n", output.ToString());
        Assert.False(new Doctor([C("github", DoctorResult.Fail("no App", "factory init"))]).Run(TextWriter.Null));
    }

    [Fact]
    public void A_check_that_throws_is_a_fail_not_a_crash()
    {
        var c = Substitute.For<IDoctorCheck>();
        c.Name.Returns("x");
        c.Check().Throws(new InvalidOperationException("boom"));
        var output = new StringWriter { NewLine = "\n" };

        Assert.False(new Doctor([c]).Run(output));
        Assert.StartsWith("fail  x: the check itself failed: boom", output.ToString(), StringComparison.Ordinal);
    }
}
