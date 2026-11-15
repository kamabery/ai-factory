using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Factory.Store;

namespace Factory.TestHost;

/// <summary>
/// A GitHub App the stub (<see cref="GitHubStubApp"/>) already knows, as `factory init` would have left it: its app.json
/// (a real RSA key, so the factory's JWT is checked) and one installation on <c>login</c> (id 77) under the stub's git
/// base, and its secrets in a FACTORY_SECRET_DIR, with a user token that does not expire. Since spec 15 the App is the
/// only GitHub credential, so every test and the simulator that talks to the stub signs in this way.
/// </summary>
public static class SimApp
{
    public const long InstallationId = 77;

    public static void Seed(string gitBase, string login, string secretDir)
    {
        Directory.CreateDirectory(gitBase);
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        File.WriteAllText(Path.Combine(gitBase, "app.json"), new JsonObject
        {
            ["id"] = 1001L, ["slug"] = "ai-factory-sim", ["name"] = "AI factory (sim)", ["client_id"] = "Iv1.sim", ["client_secret"] = "sim-secret",
            ["pem"] = pem, ["owner"] = new JsonObject { ["login"] = login }, ["permissions"] = new JsonObject(),
        }.ToJsonString());
        File.WriteAllText(Path.Combine(gitBase, "installations.json"),
            new JsonArray(new JsonObject { ["id"] = InstallationId, ["account"] = login, ["selection"] = "all", ["permissions"] = new JsonObject() }).ToJsonString());
        var store = new FileSecretStore(secretDir);
        store.Set(SecretNames.AppId, "1001");
        store.Set(SecretNames.AppSlug, "ai-factory-sim");
        store.Set(SecretNames.AppClientId, "Iv1.sim");
        store.Set(SecretNames.AppClientSecret, "sim-secret");
        store.Set(SecretNames.AppPrivateKey, pem);
        store.Set(SecretNames.UserToken, "ghu_sim");
    }
}
