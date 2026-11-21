using System.Collections;
using Factory.Runner;
using Factory.Store;

// The job runner (contract, "Runner"), started by `factory runner`, the server's watchdog, or the AppHost.
// Its settings files sit beside it, wherever it is started from.
// --one (a job execution) is the runner's own flag, not configuration.
var one = args.Contains("--one");
args = [.. args.Where(a => a != "--one")];
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.AddServiceDefaults();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.OrdinalIgnoreCase);
builder.Services.AddFactoryRunner(env, Environment.CurrentDirectory, Path.GetTempPath(),
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), builder.Configuration["Factory:Mode"], one);
builder.Services.AddHostedService<RunnerService>();

using var host = builder.Build();
// The run mode decides who pays: with none set at all, refuse to start rather than default to a subscription.
if (host.Services.GetRequiredService<LlmProvider>().Mode is null)
{
    Console.Error.WriteLine("runner: no run mode: set FACTORY_MODE (local or cloud) in the environment or .env, or Factory:Mode in configuration");
    return 1;
}
host.Run();
return 0;
