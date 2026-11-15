using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>
/// factory/github-app.json as init posts it. GitHub ignores a manifest it cannot use and shows its blank "Register new
/// GitHub App" form instead (spec 14's first live run: a webhook block with no url).
/// </summary>
public sealed class AppManifestTests
{
    private static AppManifest Manifest() =>
        new(new FactoryPaths(Options.Create(new FactoryOptions { Home = Path.GetTempPath(), Scripts = GitHubStub.RepoFile("factory", "scripts") }),
            new GitClient(new ProcessRunner())), new FileStore());

    [Fact]
    public void The_posted_manifest_has_what_GitHub_requires_and_no_webhook_block_without_a_url()
    {
        var m = Manifest().For("kamabery", "http://127.0.0.1:5000");

        Assert.Equal(("AI factory (kamabery)", "https://github.com/kamabery", "http://127.0.0.1:5000/callback"),
            ((string)m["name"]!, (string)m["url"]!, (string)m["redirect_url"]!));
        Assert.Equal("http://127.0.0.1/oauth", (string)m["callback_urls"]![0]!);
        Assert.True(m["hook_attributes"] is null || m["hook_attributes"]!["url"] is not null, "GitHub needs a hook url with hook_attributes");
        Assert.Equal("read", Manifest().Permissions()["metadata"]);
    }

    // GitHub Apps' repository permission names, as /app-manifests and an installation's permissions spell them. A name
    // outside the list (a fine-grained PAT's "variables") makes GitHub refuse the manifest: "Default permission records
    // resource is not included in the list" (spec 14's live run).
    private static readonly string[] AppRepositoryPermissions =
    [
        "actions", "actions_variables", "administration", "attestations", "checks", "contents", "deployments", "discussions", "environments",
        "issues", "merge_queues", "metadata", "packages", "pages", "pull_requests", "repository_custom_properties", "repository_hooks",
        "repository_projects", "secret_scanning_alerts", "secrets", "security_events", "single_file", "statuses", "vulnerability_alerts", "workflows",
    ];

    [Fact]
    public void Every_permission_the_manifest_asks_for_is_a_GitHub_App_permission()
    {
        Assert.Empty(Manifest().Permissions().Keys.Except(AppRepositoryPermissions));
    }
}
