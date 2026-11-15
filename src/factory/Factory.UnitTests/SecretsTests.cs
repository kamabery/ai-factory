using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

public class SecretsTests
{
    private readonly ISecretStore _store = Substitute.For<ISecretStore>();
    private readonly IDotEnv _dotEnv = Substitute.For<IDotEnv>();

    private Secrets Sut(params (string, string?)[] environment) =>
        new(Options.Create(new SecretOptions { Environment = environment.ToDictionary(e => e.Item1, e => e.Item2) }), _store, _dotEnv);

    [Fact]
    public void The_environment_wins_then_the_store_then_env()
    {
        _store.Get("HCP_TOKEN").Returns("from-store");
        _dotEnv.Get("HCP_TOKEN").Returns("from-env-file");
        _dotEnv.Get("GITHUB_APP_SLUG").Returns("slug-in-env-file");

        Assert.Equal("from-process", Sut(("HCP_TOKEN", "from-process")).Get("HCP_TOKEN"));
        Assert.Equal("from-store", Sut().Get("HCP_TOKEN"));
        Assert.Equal("slug-in-env-file", Sut().Get("GITHUB_APP_SLUG"));
        Assert.Null(Sut().Get("GITHUB_APP_ID"));
    }

    [Fact]
    public void An_empty_value_counts_as_unset()
    {
        _store.Get("HCP_TOKEN").Returns("");
        _dotEnv.Get("HCP_TOKEN").Returns("from-env-file");

        Assert.Equal("from-env-file", Sut(("HCP_TOKEN", "")).Get("HCP_TOKEN"));
    }

    [Fact]
    public void Lists_the_secrets_env_still_holds_in_order_and_not_the_retired_GitHub_PAT()
    {
        _dotEnv.Get("HCP_TOKEN").Returns("x");
        _dotEnv.Get("GITHUB_APP_ID").Returns("y");
        _dotEnv.Get("GITHUB_TOKEN").Returns("ghp_x");

        Assert.Equal(["GITHUB_APP_ID", "HCP_TOKEN"], Sut().InEnvFile());
    }
}
