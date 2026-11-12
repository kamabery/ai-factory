// The factory on .NET (roadmap spec 08): the dashboard server and the runner, together. Both run with FACTORY_IMPL=dotnet,
// so factory.ps1 forwards its ported commands to the CLI, and every runner the server or its watchdog starts through
// runner.ps1 is the .NET runner too. The runner resource drains what is approved at start, then exits; the server
// starts it again on each approval. The runner's mode is runner.ps1's default, Factory:Mode=local, unless FACTORY_MODE
// passes through. FACTORY_HOME and FACTORY_ENV_FILE pass through when set (the server takes the .env as --env-file,
// as serve.ps1 took -EnvFile), else both use this checkout's .factory-runs and .env.
var builder = DistributedApplication.CreateBuilder(args);

static string[] PassThrough() => ["FACTORY_HOME", "FACTORY_ENV_FILE", "FACTORY_MODE", "FACTORY_FOUNDRY_RESOURCE", "FACTORY_WORK"];

string[] serverArgs = Environment.GetEnvironmentVariable("FACTORY_ENV_FILE") is { Length: > 0 } envFile
    ? ["--port", "4317", "--env-file", envFile]
    : ["--port", "4317"];

var server = builder.AddProject<Projects.Factory_Server>("server")
    .WithArgs(serverArgs)
    .WithHttpEndpoint(port: 4317, isProxied: false)
    .WithEnvironment("FACTORY_IMPL", "dotnet");

var runner = builder.AddProject<Projects.Factory_Runner>("runner")
    .WithEnvironment("FACTORY_IMPL", "dotnet")
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
