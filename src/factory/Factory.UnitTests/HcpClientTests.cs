using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class HcpClientTests
{
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();
    private readonly StubHttpHandler _http = new();

    public HcpClientTests() => _env.Get(Arg.Any<string>()).Returns((string?)null);

    private HcpClient Sut => new(new HttpClient(_http), _env);

    [Fact]
    public void Reads_the_varsets_variables_with_the_team_token()
    {
        _env.Get("HCP_AZURE_VARSET").Returns("vs-1");
        _env.Get("HCP_TOKEN").Returns("tok");
        _env.Get("HCP_API_URL").Returns("http://hcp/");
        _http.Respond = r =>
        {
            Assert.Equal("http://hcp/api/v2/varsets/vs-1/relationships/vars", r.RequestUri!.ToString());
            Assert.Equal("Bearer tok", r.Headers.Authorization!.ToString());
            return """{"data":[{"attributes":{"category":"env","key":"K","value":"v","sensitive":false}},{"attributes":{"category":"terraform","key":"S","value":null,"sensitive":true}}]}""";
        };

        Assert.Equal([new HcpVar("env", "K", "v", false), new HcpVar("terraform", "S", null, true)], Sut.VarsetVars());
    }

    [Fact]
    public void No_varset_is_no_variables_and_no_call()
    {
        Assert.Empty(Sut.VarsetVars());
        Assert.Equal(0, _http.Calls);
    }

    [Fact]
    public void Needs_a_token()
    {
        _env.Get("HCP_AZURE_VARSET").Returns("vs-1");

        Assert.Equal("HCP_TOKEN is not set in .env", Assert.Throws<FactoryException>(Sut.VarsetVars).Message);
    }
}
