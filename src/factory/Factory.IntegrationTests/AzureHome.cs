using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary>
/// One FACTORY_HOME on a container of the blob stub, with the real store stack: two of these on one container
/// stand in for two machines, as state-factory.Tests.ps1's two homes do.
/// </summary>
public sealed class AzureHome
{
    public AzureHome(string home, string containerUrl, string token = BlobStub.Token, long? cap = null)
    {
        Home = home;
        Directory.CreateDirectory(home);
        Options = new StateOptions { State = "azure", BlobUrl = containerUrl, Token = token, Cap = cap?.ToString(), LocalDir = System.IO.Path.Combine(home, ".local") };
        var paths = new FactoryPaths(Microsoft.Extensions.Options.Options.Create(new FactoryOptions { Home = home, EnvFile = System.IO.Path.Combine(home, "none.env") }), new GitClient(new ProcessRunner()));
        Files = new FileStore();
        var opts = Microsoft.Extensions.Options.Options.Create(Options);
        var settings = Settings = new StateSettings(opts, paths, new DotEnv(paths, Files));
        Blobs = new BlobClient(new HttpClient(), settings, new StateToken(opts, Files, new ProcessRunner(), new HttpClient(), TimeProvider.System), TimeProvider.System);
        Sync = new StateSync(paths, Files, Blobs, settings, TimeProvider.System);
        Store = new FactoryStore(paths, Files, new BoardMutex(paths), settings, opts, Blobs, Sync, TimeProvider.System);
    }

    public string Home { get; }
    public StateOptions Options { get; }
    public StateSettings Settings { get; }
    public FileStore Files { get; }
    public BlobClient Blobs { get; }
    public StateSync Sync { get; }
    public FactoryStore Store { get; }

    public string Path(string rel) => System.IO.Path.Combine(Home, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Put(string rel, string text)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path(rel))!);
        File.WriteAllText(Path(rel), text);
    }
}
