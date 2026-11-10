using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class DotEnvTests
{
    [Fact]
    public void Reads_the_env_file_once_and_an_empty_value_is_unset()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.EnvFile.Returns("x.env");
        var files = Substitute.For<IFileStore>();
        files.ReadText("x.env").Returns("HCP_ORG=org\nEMPTY=\n");
        var env = new DotEnv(paths, files);

        Assert.Equal(("org", null, null), (env.Get("HCP_ORG"), env.Get("EMPTY"), env.Get("MISSING")));
        files.Received(1).ReadText("x.env");
    }

    [Fact]
    public void Outside_a_repository_there_is_no_env()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.EnvFile.Returns((string?)null);

        Assert.Null(new DotEnv(paths, Substitute.For<IFileStore>()).Get("HCP_ORG"));
    }
}
