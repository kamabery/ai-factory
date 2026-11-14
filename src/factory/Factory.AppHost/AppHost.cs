// The factory on .NET: the dashboard server and the runner, together. The runner resource drains what is approved at
// start, then exits; the server starts it again (factory runner) on each approval. The runner's mode is factory runner's
// default, Factory:Mode=local, unless FACTORY_MODE passes through. FACTORY_HOME and FACTORY_ENV_FILE pass through when
// set (the server takes the .env as --env-file), else both use this checkout's .factory-runs and .env.
var builder = DistributedApplication.CreateBuilder(args);

static string[] PassThrough() => ["FACTORY_HOME", "FACTORY_ENV_FILE", "FACTORY_MODE", "FACTORY_FOUNDRY_RESOURCE", "FACTORY_WORK"];

string[] serverArgs = Environment.GetEnvironmentVariable("FACTORY_ENV_FILE") is { Length: > 0 } envFile
    ? ["--port", "4317", "--env-file", envFile]
    : ["--port", "4317"];

var server = builder.AddProject<Projects.Factory_Server>("server")
    .WithArgs(serverArgs)
    .WithHttpEndpoint(port: 4317, isProxied: false);

var runner = builder.AddProject<Projects.Factory_Runner>("runner")
    .WithEnvironment("Factory__Mode", "local")
    .WaitFor(server);

foreach (var name in PassThrough())
{
    if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
    {
        server.WithEnvironment(name, value);
        runner.WithEnvironment(name, value);
    }
}

builder.Build().Run();
