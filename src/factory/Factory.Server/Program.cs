using System.Collections;
using System.Net;
using System.Net.Sockets;
using Factory.Server;
using Factory.Server.Abstractions;
using Factory.Store.Abstractions;

// The dashboard server (roadmap spec 08): serve.ps1, ported. serve.ps1 forwards here when FACTORY_IMPL=dotnet.
// Arguments are serve.ps1's: --port, --state-dir, --env-file, --watchdog-seconds, --pr-sync-seconds.
var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.OrdinalIgnoreCase);
var cli = new ConfigurationBuilder().AddCommandLine(args).Build();
var scripts = env.GetValueOrDefault("FACTORY_SCRIPTS") is { Length: > 0 } s ? s : Factory.Store.FactoryPaths.FindScripts(AppContext.BaseDirectory)
    ?? throw new InvalidOperationException("cannot find factory/scripts above the .NET server; set FACTORY_SCRIPTS");
// serve.ps1: -EnvFile, else the .env beside factory/. factory.ps1 reads the GitHub settings from the same file.
var envFile = Path.GetFullPath(cli["env-file"] is { Length: > 0 } e ? e : Path.Combine(scripts, "..", "..", ".env"));
var port = int.TryParse(cli["port"], out var p) ? p : 0;
var watchdog = int.TryParse(cli["watchdog-seconds"], out var w) ? w : 10;
var prSync = int.TryParse(cli["pr-sync-seconds"], out var ps) ? ps : 30;
var stateDir = cli["state-dir"] is { Length: > 0 } sd ? sd : null;

void Compose(IServiceCollection services) => services.AddFactoryServer(env, stateDir, scripts, envFile, port, watchdog, prSync,
    Environment.CurrentDirectory, Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

// --port 0 (the default) takes a free loopback port, so a second dashboard never collides with one already running.
// The probe frees the port before Kestrel binds it, so retry on a lost race.
WebApplication? app = null;
ServeLock? serveLock = null;
string home = "";
for (var attempt = 1; ; attempt++)
{
    var bind = port != 0 ? port : FreePort();
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.AddServiceDefaults();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Host.UseDefaultServiceProvider(o => { o.ValidateOnBuild = true; o.ValidateScopes = true; });
    builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(bind));
    Compose(builder.Services);
    var candidate = builder.Build();
    if (serveLock is null)
    {
        // The state dir, resolved as the CLI resolves it, made absolute and created; children (factory.ps1, the runner,
        // idea turns) inherit it and the .env path.
        home = Path.GetFullPath(candidate.Services.GetRequiredService<IFactoryPaths>().Home);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("FACTORY_HOME", home);
        Environment.SetEnvironmentVariable("FACTORY_ENV_FILE", envFile);
        env["FACTORY_HOME"] = home;
        env["FACTORY_ENV_FILE"] = envFile;
        stateDir = home;
        serveLock = new ServeLock(home, candidate.Services.GetRequiredService<IProcessProbe>());
        var taken = serveLock.TryTake();
        if (!taken.Taken)
        {
            Console.Error.WriteLine($"serve.ps1 refused: a dashboard (PID {taken.HolderPid}) already serves {home} at {taken.HolderUrl}");
            await candidate.DisposeAsync();
            return 1;
        }
        serveLock.Write(null);
    }
    candidate.MapDashboard();
    try
    {
        await candidate.StartAsync();
        app = candidate;
        port = bind;
        break;
    }
    catch (IOException) when (port == 0 && attempt < 5) { await candidate.DisposeAsync(); }
    catch { await candidate.DisposeAsync(); serveLock.Release(); throw; }
}

var url = $"http://localhost:{port}/";
serveLock.Write(url);
Console.WriteLine($"factory dashboard on {url}  (state: {home})");
// Previews that outlived their work, in case nothing has drained the runner since (contract, "Deploy").
app.Services.GetRequiredService<IFactoryLauncher>().StartDetached(["sweep"]);
try { await app.WaitForShutdownAsync(); }
finally
{
    await app.DisposeAsync();
    serveLock.Release();
}
return 0;

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var free = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return free;
}
