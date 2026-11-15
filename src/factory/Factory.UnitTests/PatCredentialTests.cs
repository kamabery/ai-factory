using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class PatCredentialTests
{
    [Fact]
    public void Is_the_GitHub_PAT_in_dotenv()
    {
        var env = Substitute.For<IDotEnv>();
        env.Get("GITHUB_TOKEN").Returns("ghp_x");

        var pat = new PatCredential(EnvSecrets.Over(env));

        Assert.Equal(("ghp_x", "GITHUB_TOKEN is not set in .env"), (pat.Token(), pat.Missing));
    }
}
