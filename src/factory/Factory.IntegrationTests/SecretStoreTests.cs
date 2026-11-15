using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.IntegrationTests;

/// <summary>Each secret store on the machine it runs on: a folder (tests), Credential Manager (Windows), secret-tool (Linux, where a secret service runs).</summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _prefix = $"ai-factory-test-{Guid.NewGuid():N}";

    public void Dispose() => _dir.Dispose();

    // A GitHub App's private key is about 1.7 KB of PEM; Credential Manager caps a blob at 2,560 bytes.
    private static readonly string Pem = "-----BEGIN RSA PRIVATE KEY-----\n" + string.Join("\n", Enumerable.Repeat(new string('A', 64), 25)) + "\n-----END RSA PRIVATE KEY-----\n"; // gitleaks:allow (fake key)

    private static void RoundTrip(ISecretStore store)
    {
        Assert.Null(store.Get("GITHUB_APP_PRIVATE_KEY"));
        store.Set("GITHUB_APP_PRIVATE_KEY", Pem);
        store.Set("HCP_TOKEN", "first");
        store.Set("HCP_TOKEN", "sëcond");
        Assert.Equal(Pem, store.Get("GITHUB_APP_PRIVATE_KEY"));
        Assert.Equal("sëcond", store.Get("HCP_TOKEN"));
        store.Delete("HCP_TOKEN");
        store.Delete("HCP_TOKEN");
        Assert.Null(store.Get("HCP_TOKEN"));
        store.Delete("GITHUB_APP_PRIVATE_KEY");
    }

    [Fact]
    public void A_folder_store_keeps_one_file_per_secret()
    {
        var store = new FileSecretStore(_dir["secrets"]);
        RoundTrip(store);
        store.Set("GITHUB_APP_ID", "42");

        Assert.Equal("42", File.ReadAllText(Path.Combine(_dir["secrets"], "GITHUB_APP_ID")));
        Assert.Equal($"the folder {_dir["secrets"]}", store.Name);
    }

    [Fact]
    public void Credential_Manager_keeps_secrets_for_this_user()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Credential Manager is Windows only"); return; }
        var store = new WindowsCredentialStore(_prefix);
        try { RoundTrip(store); }
        finally { foreach (var n in SecretNames.All) store.Delete(n); }
        Assert.Equal($"Windows Credential Manager ({_prefix}/...)", store.Name);
    }

    [Fact]
    public void Secret_tool_keeps_secrets_in_libsecret()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "libsecret is Linux only");
        var store = new SecretToolStore(new ProcessRunner(), _prefix);
        try { store.Set("doctor-probe", "x"); }
        catch (Core.FactoryException e) { Assert.Skip($"no secret service here: {e.Message}"); }
        try { RoundTrip(store); }
        finally { foreach (var n in SecretNames.All.Append("doctor-probe")) store.Delete(n); }
    }
}
