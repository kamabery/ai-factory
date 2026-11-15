using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

public class ToolRunnerTests
{
    private readonly IProcessRunner _processes = Substitute.For<IProcessRunner>();
    private readonly IFactoryPaths _paths = Substitute.For<IFactoryPaths>();
    private readonly DeployOptions _o = new();

    public ToolRunnerTests()
    {
        _paths.ScriptsDir.Returns("scripts");
        _processes.Run(default!, default!, default!, default, default).ReturnsForAnyArgs(new ProcessResult(3, "out\n", "err\n"));
    }

    private ToolRunner Sut => new(_processes, _paths, Options.Create(_o));

    [Fact]
    public void Gh_runs_gh_with_its_stdin_and_environment_and_merges_stdout_then_stderr()
    {
        var env = new Dictionary<string, string?> { ["GH_TOKEN"] = "pat" };

        var r = Sut.Gh(["secret", "set"], "team", env);

        Assert.Equal(new ToolResult(3, "out\nerr"), r);
        Assert.Equal(["out", "err"], r.Lines);
        _processes.Received(1).Run("gh", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "secret", "set" })), "", "team", env);
    }

    [Fact]
    public void An_override_runs_its_program_and_leading_arguments_then_the_real_arguments()
    {
        _o.GhCmd = """["dotnet", "th.dll", "tool", "--"]""";
        _o.AzCmd = "/fakes/az";

        Sut.Gh(["auth"]);
        Sut.Az(["ad", "app"]);

        _processes.Received(1).Run("dotnet", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "th.dll", "tool", "--", "auth" })), "", null, null);
        _processes.Received(1).Run("/fakes/az", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "ad", "app" })), "", null, null);
    }

    [Fact]
    public void Az_is_resolved_from_PATH_as_the_platform_names_it()
    {
        Sut.Az(["account", "show"]);
        _processes.Received(1).Run(StateToken.AzProgram, Arg.Any<IReadOnlyList<string>>(), "", null, null);
    }

    [Fact]
    public void Signin_runs_the_smoke_script_in_node_or_its_override_the_password_on_stdin_only()
    {
        Sut.Signin(["--url", "u"], "pw");
        _processes.Received(1).Run("node", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { Path.Combine("scripts", "signin-smoke.mjs"), "--url", "u" })), "", "pw", null);

        _o.SigninCmd = """["dotnet", "th.dll", "signin"]""";
        Sut.Signin(["--url", "u"], "pw");
        _processes.Received(1).Run("dotnet", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "th.dll", "signin", "--url", "u" })), "", "pw", null);
    }

    [Fact]
    public void A_scaffold_command_runs_as_its_own_program_in_the_directory_with_no_shell()
    {
        _processes.Run(default!, default!, default!, default, default).ReturnsForAnyArgs(new ProcessResult(2, "out\n", "err\n"));

        Assert.Equal(new ToolResult(2, "out\nerr"), Sut.Run("dir", "aspire-addon", ["add", "auth"]));
        _processes.Received(1).Run("aspire-addon", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "add", "auth" })), "dir", null, null);
    }

    [Fact]
    public void DeployOptions_take_their_defaults_for_anything_not_a_number()
    {
        var o = new DeployOptions { PollSeconds = "0", SmokeSeconds = "x" };
        Assert.Equal((0, 60, 300, 120, 30, 90), (o.Poll, o.Timeout, o.Smoke, o.Signin, o.SigninRetry, o.DestroyStale));
    }
}
