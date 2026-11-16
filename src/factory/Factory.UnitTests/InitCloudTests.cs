using Factory.Cli;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary><c>factory init cloud</c> (contract, "Cloud mode (deployed)"): this machine's secrets into the deployed factory's vault.</summary>
public class InitCloudTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "init-cloud-fx");
    private static readonly string Infra = Path.GetFullPath(Path.Combine(Root, "factory", "infra", "cloud"));
    private static readonly string Cwd = Path.Combine(Root, "cwd");
    private const string Vault = "https://kv-factory-cloud.vault.azure.net/";
    private readonly MemoryFiles _files = new();
    private readonly IFactoryPaths _paths = Substitute.For<IFactoryPaths>();
    private readonly IProcessRunner _proc = Substitute.For<IProcessRunner>();
    private readonly ISecretStore _local = Substitute.For<ISecretStore>();
    private readonly Dictionary<string, string> _vault = [];
    private string? _vaultUri;

    private const string Outputs = """
        {"cloud":{"sensitive":false,"type":["object",{}],"value":{"vault_uri":"https://kv-factory-cloud.vault.azure.net/","dashboard_url":"https://factory.example.azurecontainerapps.io"}}}
        """;

    public InitCloudTests()
    {
        _paths.ScriptsDir.Returns(Path.Combine(Root, "factory", "scripts"));
        _proc.Run("terraform", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(0, Outputs, ""));
        _local.Get(Arg.Any<string>()).Returns((string?)null);
        _local.Get("GITHUB_APP_ID").Returns("123");
        _local.Get("GITHUB_APP_PRIVATE_KEY").Returns("-----BEGIN KEY-----");
        _local.Get("HCP_TOKEN").Returns("hcp");
    }

    private ISecretStore VaultFor(string uri)
    {
        _vaultUri = uri;
        var v = Substitute.For<ISecretStore>();
        v.Get(Arg.Any<string>()).Returns(c => _vault.GetValueOrDefault(c.Arg<string>()));
        v.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>())).Do(c => _vault[c.ArgAt<string>(0)] = c.ArgAt<string>(1));
        return v;
    }

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var (o, e) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        var code = new InitCloud(new CliEnvironment(new Dictionary<string, string?>(), Cwd, "\n", "/appdata", "/tmp", "/bin"), _paths, _proc, _files, _local, VaultFor)
            .Run(args, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Copies_each_secret_this_machine_holds_into_the_vault_never_printing_a_value_then_names_the_dashboard()
    {
        _vault["HCP_TOKEN"] = "hcp";

        var r = Run();

        Assert.Equal((0, """
            GITHUB_APP_ID: set
            GITHUB_APP_PRIVATE_KEY: set
            HCP_TOKEN: unchanged
            dashboard: https://factory.example.azurecontainerapps.io

            """.ReplaceLineEndings("\n"), ""), r);
        Assert.Equal(Vault, _vaultUri);
        Assert.Equal(new Dictionary<string, string> { ["GITHUB_APP_ID"] = "123", ["GITHUB_APP_PRIVATE_KEY"] = "-----BEGIN KEY-----", ["HCP_TOKEN"] = "hcp" }, _vault);
        _proc.Received(1).Run("terraform", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { $"-chdir={Infra}", "output", "-json" })), Infra);
    }

    [Fact]
    public void From_reads_the_outputs_file_and_runs_no_terraform()
    {
        _files.Put(Path.Combine(Cwd, "out.json"), Outputs);

        Assert.Equal(0, Run("--from", "out.json").Code);

        _proc.DidNotReceiveWithAnyArgs().Run(default!, default!, default!);
    }

    [Theory]
    [InlineData("""{"other":{"value":1}}""", "terraform output has no cloud vault_uri")]
    [InlineData("not json", "terraform output is not JSON")]
    public void Refuses_outputs_without_the_vault_and_writes_nothing(string json, string why)
    {
        _proc.Run("terraform", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(0, json, ""));

        Assert.Equal((1, "", $"factory: init cloud refused: {why}; run terraform apply in factory/infra/cloud first\n"), Run());
        Assert.Empty(_vault);
    }

    [Fact]
    public void Refuses_a_failed_terraform_with_its_first_error_line_and_a_bad_argument()
    {
        _proc.Run("terraform", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(1, "", "╷\n│ Error: No outputs found\n╵\n"));

        Assert.Equal((1, "", "factory: init cloud refused: terraform output failed: Error: No outputs found; run terraform apply in factory/infra/cloud first\n"), Run());
        Assert.Equal((2, "", "factory: init cloud refused: usage: factory init cloud [--from <file>]\n"), Run("--bogus"));
    }
    [Fact]
    public void Refuses_when_terraform_cannot_start()
    {
        _proc.Run("terraform", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(_ => throw new System.ComponentModel.Win32Exception(267, "The directory name is invalid."));

        Assert.Equal((1, "", "factory: init cloud refused: terraform output failed: The directory name is invalid.; run terraform apply in factory/infra/cloud first\n"), Run());
    }
}
