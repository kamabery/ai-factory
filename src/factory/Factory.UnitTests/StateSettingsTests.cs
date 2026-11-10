using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

public class StateSettingsTests
{
    private const string Home = @"C:\Work\Repo\.factory-runs";

    private static StateSettings Settings(StateOptions o, string? envFile = null)
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.Home.Returns(Home);
        paths.EnvFile.Returns(envFile is null ? null : "x.env");
        var files = Substitute.For<IFileStore>();
        files.ReadText("x.env").Returns(envFile);
        return new StateSettings(Options.Create(o), paths, new DotEnv(paths, files));
    }

    [Fact]
    public void Local_unless_FACTORY_STATE_says_azure()
    {
        var s = Settings(new StateOptions());

        Assert.False(s.IsAzure);
        Assert.Null(s.ContainerUrl);
        Assert.Equal(1L << 30, s.Cap);
    }

    [Fact]
    public void The_environment_wins_over_env_and_env_fills_the_rest()
    {
        var s = Settings(new StateOptions { State = "azure" }, "FACTORY_STATE=local\nFACTORY_BLOB_URL=https://a/factory/\nFACTORY_STATE_CAP=20000");

        Assert.True(s.IsAzure);
        Assert.Equal("https://a/factory", s.ContainerUrl);
        Assert.Equal(20000, s.Cap);
    }

    [Fact]
    public void A_home_placeholder_becomes_the_homes_hash_so_each_test_home_gets_its_own_container()
    {
        var s = Settings(new StateOptions { State = "azure", BlobUrl = "http://localhost:1/c{home}" });

        Assert.Equal("http://localhost:1/ch5fc3b337c80872c7", s.ContainerUrl);
    }

    [Theory]
    [InlineData("cloud", null, "state: FACTORY_STATE 'cloud' must be local or azure")]
    [InlineData("azure", null, "state: FACTORY_STATE=azure needs FACTORY_BLOB_URL (the container URL)")]
    public void Refuses_what_state_ps1_refuses(string state, string? url, string message)
    {
        var s = Settings(new StateOptions { State = state, BlobUrl = url });

        Assert.Equal(message, Assert.Throws<FactoryException>(() => s.IsAzure).Message);
    }

    [Fact]
    public void Reads_env_once_through_DotEnv()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.EnvFile.Returns("x.env");
        var files = Substitute.For<IFileStore>();
        var s = new StateSettings(Options.Create(new StateOptions()), paths, new DotEnv(paths, files));

        _ = s.IsAzure;
        _ = s.Cap;

        files.Received(1).ReadText("x.env");
    }
}
