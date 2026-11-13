using Factory.Core;
using Factory.Runner;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>The run mode's provider (llm.ps1's Get-ProviderEnv), the headless settings, and the runner's composition.</summary>
public class RunnerProviderTests
{
    private readonly IDotEnv _dotEnv = Substitute.For<IDotEnv>();

    public RunnerProviderTests() => _dotEnv.Get(Arg.Any<string>()).Returns((string?)null);

    private LlmProvider Provider(string? configured = null, params (string, string)[] env) =>
        new(Options.Create(new LlmOptions { ConfiguredMode = configured, Environment = env.ToDictionary(e => e.Item1, e => (string?)e.Item2) }), _dotEnv);

    [Fact]
    public void Local_mode_removes_every_variable_that_could_route_claude_elsewhere()
    {
        var env = Provider("local").ChildEnvironment();

        Assert.Equal(LlmProvider.ProviderVars.Order(), env.Keys.Order());
        Assert.All(env.Values, Assert.Null);
    }

    [Fact]
    public void Cloud_mode_runs_on_the_foundry_resource_keyless_with_the_default_deployments_unless_one_is_pinned()
    {
        _dotEnv.Get("FACTORY_MODE").Returns("cloud");
        _dotEnv.Get("FACTORY_FOUNDRY_RESOURCE").Returns("fdy-test");
        _dotEnv.Get("ANTHROPIC_DEFAULT_SONNET_MODEL").Returns("my-sonnet");

        var env = Provider().ChildEnvironment();

        Assert.Equal(("1", "fdy-test"), (env["CLAUDE_CODE_USE_FOUNDRY"], env["ANTHROPIC_FOUNDRY_RESOURCE"]));
        Assert.Equal(("claude-opus-5-5", "my-sonnet", "claude-haiku-5-5"),
            (env["ANTHROPIC_DEFAULT_OPUS_MODEL"], env["ANTHROPIC_DEFAULT_SONNET_MODEL"], env["ANTHROPIC_DEFAULT_HAIKU_MODEL"]));
        foreach (var n in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL", "ANTHROPIC_FOUNDRY_BASE_URL", "ANTHROPIC_FOUNDRY_API_KEY", "ANTHROPIC_FOUNDRY_AUTH_TOKEN" })
            Assert.Null(env[n]);
    }

    [Fact]
    public void The_mode_comes_from_the_environment_then_env_file_then_configuration_else_none()
    {
        _dotEnv.Get("FACTORY_MODE").Returns("local");
        Assert.Equal("cloud", Provider("local", ("FACTORY_MODE", "cloud")).Mode);
        Assert.Equal("local", Provider("cloud").Mode);

        _dotEnv.Get("FACTORY_MODE").Returns((string?)null);
        Assert.Equal("cloud", Provider("cloud", ("FACTORY_MODE", "")).Mode);
        Assert.Null(Provider().Mode);
        Assert.Null(Provider("").Mode);
    }

    [Fact]
    public void The_environment_beats_env_file_for_the_foundry_settings_too()
    {
        _dotEnv.Get("FACTORY_FOUNDRY_RESOURCE").Returns("from-file");

        var env = Provider("cloud", ("FACTORY_FOUNDRY_RESOURCE", "from-env")).ChildEnvironment();

        Assert.Equal("from-env", env["ANTHROPIC_FOUNDRY_RESOURCE"]);
    }

    [Fact]
    public void A_bad_mode_or_cloud_with_no_foundry_resource_refuses()
    {
        Assert.Equal("FACTORY_MODE 'openrouter' must be local or cloud", Assert.Throws<FactoryException>(() => Provider("openrouter").ChildEnvironment()).Message);
        Assert.Equal("no FACTORY_FOUNDRY_RESOURCE", Assert.Throws<FactoryException>(() => Provider("cloud").ChildEnvironment()).Message);
    }

    // --- headless settings -----------------------------------------------------------------------

    [Fact]
    public void Headless_settings_allow_the_toolchain_and_factory_ps1_in_both_shells_and_fence_edits_and_reads()
    {
        var root = Path.Combine(Path.GetTempPath(), "fac");
        var s = HeadlessSettings.Build(root, Path.Combine(Path.GetTempPath(), "tp"), Path.Combine(root, ".factory-runs"));

        Assert.Equal("5400000", (string?)s["env"]!["BASH_MAX_TIMEOUT_MS"]);
        var allow = s["permissions"]!["allow"]!.AsArray().Select(r => (string?)r).ToList();
        var deny = s["permissions"]!["deny"]!.AsArray().Select(r => (string?)r).ToList();
        var factory = Path.Combine(root, "factory", "scripts", "factory.ps1").Replace('\\', '/');
        Assert.Contains($"Bash(pwsh -NoProfile -File {factory} *)", allow);
        Assert.Contains($"PowerShell(pwsh -NoProfile -File {factory} *)", allow);
        Assert.Contains("Bash(dotnet *)", allow);
        Assert.Contains("PowerShell(git merge --no-edit factory/base*)", allow);
        Assert.DoesNotContain("Bash(printenv*)", allow);
        Assert.Contains("Bash(git push*)", deny);
        Assert.Contains("PowerShell(Get-Content env:*)", deny);
        Assert.Contains("Edit(/factory/**)", deny);
        Assert.Contains($"Edit({HeadlessSettings.RulePath(Path.Combine(root, ".factory-runs"))}/jobs/*/spec.md)", deny);
        Assert.Contains($"Read({HeadlessSettings.RulePath(Path.Combine(root, ".env"))})", deny);
        Assert.Equal(2 * HeadlessSettings.AllowCommands.Length + 2, allow.Count);
        Assert.Equal(2, s["permissions"]!["additionalDirectories"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(@"D:\code\ai-factory\.env", "//d/code/ai-factory/.env")]
    [InlineData(@"C:\x", "//c/x")]
    public void A_windows_rule_path_is_double_slashed_with_a_lower_case_drive(string path, string rule)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(rule, HeadlessSettings.RulePath(path));
    }

    [Fact]
    public void A_unix_rule_path_gets_one_more_leading_slash()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("//home/k/.env", HeadlessSettings.RulePath("/home/k/.env"));
    }

    // --- composition -----------------------------------------------------------------------------

    [Fact]
    public void The_runner_container_builds_validated_and_binds_its_settings()
    {
        var env = new Dictionary<string, string?> { ["FACTORY_CLAUDE_CMD"] = "fake", ["FACTORY_STATE_RENEW_SECONDS"] = "5", ["FACTORY_HOME"] = "h" };
        var services = new ServiceCollection().AddFactoryRunner(env, "w", "/tmp", "/appdata", "local");

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<RunnerLoop>());
        Assert.Equal(("fake", "local"), (provider.GetRequiredService<IOptions<RunnerOptions>>().Value.ClaudeCmd,
            provider.GetRequiredService<IOptions<LlmOptions>>().Value.ConfiguredMode));
        Assert.Equal("5", provider.GetRequiredService<IOptions<Factory.Store.StateOptions>>().Value.RenewSeconds);
        Assert.Same(provider.GetRequiredService<LlmProvider>(), provider.GetRequiredService<Factory.Store.Abstractions.ILlmProvider>());
    }

    [Fact]
    public void Started_without_FACTORY_SCRIPTS_the_runner_finds_factory_scripts_above_its_binaries()
    {
        // As the AppHost starts it: not through runner.ps1, which sets FACTORY_SCRIPTS.
        var services = new ServiceCollection().AddFactoryRunner(new Dictionary<string, string?> { ["FACTORY_HOME"] = "h" }, "w", "/tmp", "/appdata", "local");

        using var provider = services.BuildServiceProvider();

        var scripts = provider.GetRequiredService<Factory.Store.Abstractions.IFactoryPaths>().ScriptsDir;
        Assert.True(File.Exists(Path.Combine(scripts, "factory.ps1")), scripts);
    }
}
