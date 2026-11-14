using Factory.Data;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>One FACTORY_HOME on any backend, with the store stack the CLI composes: what <see cref="StoreContract"/> runs on.</summary>
public sealed class StoreHome
{
    public StoreHome(string home, StateOptions options)
    {
        Home = home;
        Directory.CreateDirectory(home);
        options.LocalDir ??= Path.Combine(home, ".local");
        var paths = Paths = new FactoryPaths(Options.Create(new FactoryOptions { Home = home, EnvFile = Path.Combine(home, "none.env") }), new GitClient(new ProcessRunner()));
        var files = new FileStore();
        var opts = Options.Create(options);
        var settings = new StateSettings(opts, paths, new DotEnv(paths, files));
        var blobs = new BlobClient(new HttpClient(), settings, new StateToken(opts, files, new ProcessRunner(), new HttpClient(), TimeProvider.System), TimeProvider.System);
        var sync = new StateSync(paths, files, blobs, settings, TimeProvider.System);
        var rows = new EfBoardRows(new FactoryDbs(new StateDbConnection(settings)));
        var database = new DatabaseStore(paths, files, rows, new StateExport(paths, files, rows, new ThreadPause()));
        Store = new FactoryStore(paths, files, new BoardMutex(paths), settings, opts, blobs, sync, TimeProvider.System, database);
    }

    public string Home { get; }

    public FactoryPaths Paths { get; }

    public IFactoryStore Store { get; }
}
