using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>Starting the runner (contract, "Cloud mode (deployed)", Starting the runner): a local process, or one execution of the runner job.</summary>
public class RunnerStarterTests
{
    private const string Job = "/subscriptions/s/resourceGroups/rg-factory-cloud/providers/Microsoft.App/jobs/factory-runner";
    private readonly IFactoryLauncher _launcher = Substitute.For<IFactoryLauncher>();
    private readonly IProcessRunner _processes = Substitute.For<IProcessRunner>();

    private RunnerStarter Starter(string? job, string? azCmd = null) =>
        new(Options.Create(new CloudOptions { RunnerJob = job }), Options.Create(new StateOptions { AzCmd = azCmd }), _launcher, _processes);

    [Fact]
    public void Without_a_runner_job_it_starts_factory_runner_detached()
    {
        var s = Starter(null);

        Assert.False(s.StartsExecutions);
        Assert.Equal("started runner", s.Start());
        _launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "runner" })));
        _processes.DidNotReceiveWithAnyArgs().Run(default!, default!, default!);
    }

    [Fact]
    public void With_a_runner_job_it_starts_one_execution_through_az_rest_and_names_it()
    {
        _processes.Run(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>())
            .Returns(new ProcessResult(0, $$"""{"id":"{{Job}}/executions/factory-runner-k3x9","name":"factory-runner-k3x9"}""", ""));
        var s = Starter(Job);

        Assert.True(s.StartsExecutions);
        Assert.Equal("started runner execution factory-runner-k3x9", s.Start());
        _processes.Received(1).Run(StateToken.AzProgram, Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[]
            { "rest", "--method", "post", "--url", $"https://management.azure.com{Job}/start?api-version=2025-01-01" })), "");
        _launcher.DidNotReceiveWithAnyArgs().StartDetached(default!);
    }

    [Fact]
    public void FACTORY_AZ_CMD_stands_in_for_az_and_a_refusal_says_why()
    {
        _processes.Run(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(1, "", "ERROR: (AuthorizationFailed) no\n"));

        var e = Assert.Throws<FactoryException>(() => Starter(Job, """["dotnet","th.dll","tool"]""").Start());

        Assert.Equal("runner job start failed: ERROR: (AuthorizationFailed) no", e.Message);
        _processes.Received(1).Run("dotnet", Arg.Is<IReadOnlyList<string>>(a => a.Take(3).SequenceEqual(new[] { "th.dll", "tool", "rest" })), "");
    }
}
