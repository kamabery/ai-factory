#:sdk Aspire.AppHost.Sdk@13.5.1
#:package Aspire.Hosting.JavaScript@13.5.1
// <<addon-packages>> - scaffolding inserts add-on #:package directives above this line.
#:property AspireUseCliBundle=true

// AddCSharpApp rather than AddProject<T>. A file-based AppHost has no .csproj to hang
// ProjectReference items off, so there is no generated Projects namespace to bind to - and for the
// MAUI host that is the feature, not the limitation. An AppHost that referenced it would have to be
// reference-compatible with net10.0-windows10.0.19041.0 and would pull the MAUI workload into every
// `aspire run`. AddCSharpApp takes a path and needs no reference at all. It still returns a
// ProjectResource, so WithReference / WithHttpHealthCheck / PublishAsStaticWebsite all behave.
#pragma warning disable ASPIRECSHARPAPPS001

var builder = DistributedApplication.CreateBuilder(args);

// Paths are relative to this file's directory, not the repository root.

// The forecast lives in a store rather than in the API's memory. Every provider is wired here, and
// the same Database:Provider value is forwarded to the API below as Database__Provider, so the
// store that starts and the EF provider that talks to it cannot disagree. The dbProvider
// scaffolding parameter collapses this to whichever single branch was asked for.
//
// WithDataVolume on the container branches: rows survive `aspire stop`, which is why BookSeeder
// checks staleness rather than emptiness. Drop the volume to start clean.
var databaseProvider = builder.Configuration["Database:Provider"] ?? "sqlite";

// One nullable slot, filled in by whichever provider branch is live. sqlite leaves it null on
// purpose: it is a local file, not a service, so there is no container to declare and nothing to
// wait for - which is what lets `aspire run` come up on a machine with no Docker at all.
//
// Each branch carries both a runtime guard and a scaffolding conditional. In this workspace all of
// them are present and Database:Provider picks between them at run time; in a scaffold only the
// provider that was asked for survives, and the guard it keeps is simply always true.
IResourceBuilder<IResourceWithConnectionString>? appdb = null;




// The API picks up the OTEL_* environment variables the AppHost injects and wires them up in
// AddServiceDefaults() (see shelf.servicedefaults).
//
// The "http" profile is pinned rather than left to Aspire's default, for two reasons. Both Blazor
// hosts read a hard-coded ApiBaseAddress (wwwroot/appsettings.json for WASM, the embedded
// appsettings.json for MAUI), so the API's port has to be deterministic. And serving the WASM page
// over https while that address stays http is a mixed-content block in the browser.
var api = builder.AddCSharpApp("api", "../src/shelf.api/shelf.api.csproj",
                               options => options.LaunchProfileName = "http")
    .WithEnvironment("Database__Provider", databaseProvider)
    // HttpProtobuf rather than the default gRPC, for the browser's sake rather than the API's. The
    // Blazor WebAssembly host cannot read this AppHost's environment and cannot commit the
    // dashboard's per-run API key, so it asks the API for the OTLP settings instead
    // (/client-telemetry/config, Development only). A browser cannot speak gRPC, so the endpoint the
    // API hands out has to be one it can post to - and the simplest way to guarantee that is for the
    // API to be using it itself.
    .WithOtlpExporter(OtlpProtocol.HttpProtobuf)
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints()
    // Point the dashboard's link for this resource at the Swagger UI page rather than the API
    // root, which 404s - nothing is routed at "/". WithUrlForEndpoint rewrites the endpoint's
    // existing URL instead of adding a second one, and the relative path is resolved against the
    // endpoint, so this survives a port change even though the http profile is pinned above.
    // Resource URLs are a run-mode affordance; Aspire ignores them on publish.
    .WithUrlForEndpoint("http", url =>
    {
        url.DisplayText = "API docs";
        url.Url = "/api-docs";
    });

if (appdb is not null)
{
    // WithReference here, unlike blazor-web below, because the API is a .NET process that reads
    // ConnectionStrings:appdb straight out of its configuration. WaitFor is not optional: the
    // API migrates and seeds at startup, so racing the container throws rather than retrying.
    api.WithReference(appdb).WaitFor(appdb);
}
else
{
    // The sqlite branch. Same configuration key the container branches populate via WithReference,
    // set by hand because there is no resource to reference. Relative to the API's working
    // directory, so the file lands beside the built API rather than anywhere absolute.
    api.WithEnvironment("ConnectionStrings__appdb", "Data Source=app.db");
}

// <<addon-resources>> - scaffolding inserts add-on resource declarations below this line.
// React, on the Vite dev server.
//   WithReference(api)  -> API_HTTP / API_HTTPS, read by vite.config.ts for the /api proxy.
//   WithOtlpExporter    -> OTLP endpoint + api key for browser RUM. HttpProtobuf is required:
//                          browsers cannot speak the gRPC protocol the .NET services use.
var ui = builder.AddViteApp("ui", "../src/ui/react")
    .WithNpm()
    .WithReference(api)
    .WaitFor(api)
    .WithOtlpExporter(OtlpProtocol.HttpProtobuf)
    .WithExternalHttpEndpoints();

// <<addon-publish>> - scaffolding inserts add-on publish-time configuration below this line.
// On publish the React UI becomes a static site behind YARP, which also reverse-proxies /api to
// the API so the browser stays same-origin exactly as it does in dev.
#pragma warning disable ASPIREJAVASCRIPT001
ui.PublishAsStaticWebsite("/api", api);
#pragma warning restore ASPIREJAVASCRIPT001

builder.Build().Run();
