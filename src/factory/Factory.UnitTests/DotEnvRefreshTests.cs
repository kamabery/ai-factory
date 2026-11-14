using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class DotEnvRefreshTests
{
    [Fact]
    public void Reads_once_until_refreshed_so_init_sees_the_env_it_wrote()
    {
        var paths = Substitute.For<IFactoryPaths>();
        paths.EnvFile.Returns("x.env");
        var files = new MemoryFiles();
        var env = new DotEnv(paths, files);

        Assert.Null(env.Get("GITHUB_WEB_URL"));
        files.Put("x.env", "GITHUB_WEB_URL=http://stub");
        Assert.Null(env.Get("GITHUB_WEB_URL"));

        env.Refresh();

        Assert.Equal("http://stub", env.Get("GITHUB_WEB_URL"));
    }
}
