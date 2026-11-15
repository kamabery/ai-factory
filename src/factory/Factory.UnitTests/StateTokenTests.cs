using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Factory.UnitTests;

public class StateTokenTests
{
    private static readonly string Local = Path.Combine(Path.GetTempPath(), "token-local");
    private static readonly string Cache = Path.Combine(Local, "storage-token.json");
    private static readonly string[] AzArgs = ["account", "get-access-token", "--resource", "https://storage.azure.com", "--output", "json"];
    private readonly MemoryFiles _files = new();
    private readonly IProcessRunner _processes = Substitute.For<IProcessRunner>();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.FromUnixTimeSeconds(1_000_000));
    private readonly StubHttpHandler _http = new();

    private StateToken Token(StateOptions o)
    {
        o.LocalDir ??= Local;
        return new StateToken(Options.Create(o), _files, _processes, new HttpClient(_http), _clock);
    }

    private void Az(int code, string stdout, string stderr = "") =>
        _processes.Run(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(code, stdout, stderr));

    [Fact]
    public void FACTORY_STATE_TOKEN_wins()
    {
        Assert.Equal("t", Token(new StateOptions { Token = "t" }).Get());
        _processes.DidNotReceiveWithAnyArgs().Run(default!, default!, default!);
    }

    [Fact]
    public void A_managed_identity_answers_through_its_endpoint()
    {
        _http.Respond = r =>
        {
            Assert.Equal("http://id/token?resource=https://storage.azure.com&api-version=2019-08-01", r.RequestUri!.ToString());
            Assert.Equal("h", r.Headers.GetValues("X-IDENTITY-HEADER").Single());
            return """{"access_token":"mi","expires_on":"2000000"}""";
        };
        var t = Token(new StateOptions { IdentityEndpoint = "http://id/token", IdentityHeader = "h" });

        Assert.Equal("mi", t.Get());
        Assert.Equal("mi", t.Get());
        Assert.Equal(1, _http.Calls);
    }

    [Fact]
    public void Asks_az_caches_the_token_outside_the_home_and_asks_again_only_near_expiry()
    {
        Az(0, """{"accessToken":"az1","expires_on":1000600}""");
        var t = Token(new StateOptions());

        Assert.Equal("az1", t.Get());
        Assert.Equal("az1", Token(new StateOptions()).Get());   // a second process: the cache
        _processes.Received(1).Run(StateToken.AzProgram, Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(AzArgs)), Arg.Any<string>());
        Assert.Contains("\"az1\"", _files.Text(Cache));

        _clock.Advance(TimeSpan.FromSeconds(301));   // within five minutes of expiry
        Az(0, """{"accessToken":"az2","expires_on":1100000}""");
        Assert.Equal("az2", t.Get());
    }

    [Fact]
    public void FACTORY_AZ_CMD_stands_in_for_az_and_a_local_expiresOn_is_read()
    {
        Az(0, """{"accessToken":"cmd","expiresOn":"2099-01-01 00:00:00.000000"}""");

        Assert.Equal("cmd", Token(new StateOptions { AzCmd = """["dotnet", "th.dll", "tool", "--"]""" }).Get());

        _processes.Received(1).Run("dotnet", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "th.dll", "tool", "--" }.Concat(AzArgs))), Arg.Any<string>());
    }

    [Fact]
    public void A_failed_az_says_so()
    {
        Az(1, "", "Please run 'az login'.\n");

        Assert.Equal("state: az account get-access-token failed (is az logged in?): Please run 'az login'.",
            Assert.Throws<FactoryException>(() => Token(new StateOptions()).Get()).Message);
    }

    [Fact]
    public void A_corrupt_cache_is_ignored()
    {
        _files.Put(Cache, "not json");
        Az(0, """{"accessToken":"fresh","expires_on":2000000}""");

        Assert.Equal("fresh", Token(new StateOptions()).Get());
    }
}
