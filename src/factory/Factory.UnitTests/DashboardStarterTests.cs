using Factory.Cli;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class DashboardStarterTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "proof", "home");
    private static readonly string Env = Path.Combine(Path.GetTempPath(), "proof", ".env");
    private readonly MemoryFiles _files = new();
    private readonly IFactoryLauncher _launcher = Substitute.For<IFactoryLauncher>();

    private DashboardStarter Sut()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.EnvFile.Returns(Env);
        return new DashboardStarter(paths, _files, _launcher);
    }

    [Fact]
    public void A_new_server_runs_on_this_state_and_this_env_never_the_main_checkouts()
    {
        Assert.Equal("http://localhost:5100/", Sut().Start(_ => false, 5100));

        _launcher.Received(1).StartDetached(Arg.Is<IReadOnlyList<string>>(a =>
            a.SequenceEqual(new[] { "serve", "--port", "5100", "--state-dir", Home, "--env-file", Env })));
    }

    [Fact]
    public void A_server_already_holding_serve_lock_is_reused()
    {
        _files.Put(Path.Combine(Home, "serve.lock"), "4242\nhttp://localhost:4317/\n");

        Assert.Equal("http://localhost:4317/", Sut().Start(pid => pid == 4242, 5100));

        _launcher.DidNotReceiveWithAnyArgs().StartDetached(default!);
    }

    [Fact]
    public void A_lock_whose_server_is_gone_starts_a_new_one()
    {
        _files.Put(Path.Combine(Home, "serve.lock"), "4242\nhttp://localhost:4317/\n");

        Assert.Equal("http://localhost:5100/", Sut().Start(_ => false, 5100));
    }
}
