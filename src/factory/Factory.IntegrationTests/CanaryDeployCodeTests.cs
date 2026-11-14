using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary>The canary fingerprint's deploy code (contract, "Canary") names files this checkout has, so a rename fails here.</summary>
public sealed class CanaryDeployCodeTests
{
    [Fact]
    public void Every_deploy_code_file_the_fingerprint_hashes_exists()
    {
        var missing = CanaryCheck.DeployCode.Where(f => !File.Exists(GitHubStub.RepoFile(f.Split('/')))).ToList();

        Assert.Empty(missing);
    }
}
