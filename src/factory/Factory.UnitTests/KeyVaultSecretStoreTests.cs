using System.Net;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>A deployed factory's secret store (contract, "Secrets, init and doctor"): Key Vault through its REST API.</summary>
public class KeyVaultSecretStoreTests
{
    private const string Vault = "https://kv-factory.vault.azure.net/";
    private readonly StubHttpHandler _http = new();
    private readonly IStateToken _token = Substitute.For<IStateToken>();
    private readonly List<string> _calls = [];

    public KeyVaultSecretStoreTests() => _token.Get("https://vault.azure.net").Returns("kv-token");

    private KeyVaultSecretStore Store() => new(new HttpClient(_http), _token, Vault);

    private void Answer(Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> answer) =>
        _http.Reply = r =>
        {
            Assert.Equal("Bearer kv-token", r.Headers.Authorization!.ToString());
            _calls.Add($"{r.Method} {r.RequestUri!.PathAndQuery}{(r.Content is null ? "" : " " + r.Content.ReadAsStringAsync().Result)}");
            var (status, body) = answer(r);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        };

    [Fact]
    public void Is_named_for_the_vault()
    {
        Assert.Equal("Key Vault kv-factory.vault.azure.net", Store().Name);
    }

    [Fact]
    public void Get_reads_the_secret_with_each_underscore_as_a_dash_and_a_missing_one_is_null()
    {
        Answer(r => r.RequestUri!.AbsolutePath.EndsWith("/GITHUB-APP-ID", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"value":"123","id":"x"}""")
            : (HttpStatusCode.NotFound, """{"error":{"code":"SecretNotFound","message":"nope"}}"""));

        Assert.Equal("123", Store().Get("GITHUB_APP_ID"));
        Assert.Null(Store().Get("HCP_TOKEN"));
        Assert.Equal(["GET /secrets/GITHUB-APP-ID?api-version=7.4", "GET /secrets/HCP-TOKEN?api-version=7.4"], _calls);
    }

    [Fact]
    public void Set_puts_the_value()
    {
        Answer(_ => (HttpStatusCode.OK, "{}"));

        Store().Set("HCP_TOKEN", "v\"1");

        Assert.Equal(["PUT /secrets/HCP-TOKEN?api-version=7.4 " + new JsonObject { ["value"] = "v\"1" }.ToJsonString()], _calls);
    }

    [Fact]
    public void Set_over_a_soft_deleted_secret_purges_it_and_puts_again()
    {
        var puts = 0;
        Answer(r => r.Method == HttpMethod.Put && puts++ == 0
            ? (HttpStatusCode.Conflict, """{"error":{"code":"Conflict","message":"Secret doctor-probe is currently in a deleted but recoverable state"}}""")
            : (HttpStatusCode.OK, "{}"));

        Store().Set("doctor-probe", "v");

        Assert.Equal(["PUT /secrets/doctor-probe?api-version=7.4 {\"value\":\"v\"}", "DELETE /deletedsecrets/doctor-probe?api-version=7.4",
            "PUT /secrets/doctor-probe?api-version=7.4 {\"value\":\"v\"}"], _calls);
    }

    [Fact]
    public void Delete_deletes_then_purges_and_a_missing_secret_or_a_refused_purge_is_fine()
    {
        Answer(r => r.RequestUri!.AbsolutePath.StartsWith("/deletedsecrets", StringComparison.Ordinal)
            ? (HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"no purge"}}""")
            : (HttpStatusCode.NotFound, "{}"));

        Store().Delete("GITHUB_USER_TOKEN");

        Assert.Equal(["DELETE /secrets/GITHUB-USER-TOKEN?api-version=7.4", "DELETE /deletedsecrets/GITHUB-USER-TOKEN?api-version=7.4"], _calls);
    }

    [Fact]
    public void Any_other_failure_is_refused_with_the_vaults_message()
    {
        Answer(_ => (HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"Caller is not authorized"}}"""));

        Assert.Equal("Key Vault 403 on GET GITHUB-APP-ID: Caller is not authorized",
            Assert.Throws<FactoryException>(() => Store().Get("GITHUB_APP_ID")).Message);
    }

    [Fact]
    public void The_machines_store_chooses_the_vault_when_FACTORY_SECRET_VAULT_is_set()
    {
        Answer(_ => (HttpStatusCode.OK, """{"value":"from-vault"}"""));
        var store = new SecretStore(Options.Create(new SecretOptions { Vault = Vault, Dir = Path.GetTempPath() }), Substitute.For<IProcessRunner>(), _token, new HttpClient(_http));

        Assert.Equal(("Key Vault kv-factory.vault.azure.net", "from-vault"), (store.Name, store.Get("X")));
    }
}
