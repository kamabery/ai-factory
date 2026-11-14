using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// The GitHub App: a JWT reads the App, it is installed on GITHUB_OWNER on all repositories with every permission the
/// manifest asks for, it mints a token, and the user token reads /user. With the PAT fallback: the PAT reads /user (a warn).
/// </summary>
public sealed class GitHubCheck(
    ISecrets secrets, IGitHubApp app, IGitHubCredential credential, IGitHub github, IDotEnv dotEnv, AppManifest manifest, TimeProvider clock) : IDoctorCheck
{
    public string Name => "github";

    private string Web => (dotEnv.Get("GITHUB_WEB_URL") ?? "https://github.com").TrimEnd('/');

    public DoctorResult Check()
    {
        if (!credential.IsApp)
        {
            try { return DoctorResult.Warn($"using the GitHub PAT fallback, signed in as {github.UserLogin()}", "factory init sets up the GitHub App"); }
            catch (FactoryException e) { return DoctorResult.Fail($"no GitHub App, and the GitHub PAT does not work: {e.Message}", "factory init"); }
        }
        try
        {
            var jwt = GitHubJwt.Create(Need(SecretNames.AppId), Need(SecretNames.AppPrivateKey), clock.GetUtcNow());
            var slug = PsJsonReader.Str(app.App(jwt)["slug"]) ?? secrets.Get(SecretNames.AppSlug) ?? "?";
            var owner = dotEnv.Get("GITHUB_OWNER");
            if (owner is null) return DoctorResult.Fail("GITHUB_OWNER is not set in .env", "factory init");
            var install = app.Installations(jwt).FirstOrDefault(i => string.Equals(i.Account, owner, StringComparison.OrdinalIgnoreCase));
            if (install is null) return DoctorResult.Fail($"the App {slug} is not installed on {owner}", $"install it on All repositories: {Web}/apps/{slug}/installations/new");
            if (install.Selection != "all")
                return DoctorResult.Fail($"the App {slug} is installed on selected repositories of {owner}; a new repo would not be on its list",
                    $"set Repository access to All repositories: {Web}/settings/installations/{install.Id}");
            var missing = AppManifest.Missing(manifest.Permissions(), install.Permissions);
            if (missing.Count > 0)
                return DoctorResult.Fail($"the installation on {owner} lacks {string.Join(", ", missing)}",
                    $"accept the App's new permissions: {Web}/settings/installations/{install.Id}");
            credential.Token(owner);
            var login = github.UserLogin();
            return DoctorResult.Ok($"the App {slug} on {owner} (all repositories), signed in as {login}");
        }
        catch (FactoryException e) { return DoctorResult.Fail(e.Message, "factory init"); }
    }

    private string Need(string name) => secrets.Get(name) ?? throw new FactoryException($"{name} is not in the secret store");
}
