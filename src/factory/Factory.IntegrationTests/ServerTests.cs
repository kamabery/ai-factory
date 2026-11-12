using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Factory.Server;
using Factory.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Factory.IntegrationTests;

/// <summary>The dashboard server over real HTTP on a real disk: routing, the path rules, file serving and serve.lock.</summary>
public sealed class ServerTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private WebApplication? _app;
    private HttpClient _http = new();

    private string Home => _dir["state"];

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Home);
        var scripts = FactoryPaths.FindScripts(AppContext.BaseDirectory)!;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Services.AddFactoryServer(new Dictionary<string, string?> { ["FACTORY_WORK"] = _dir["work"] }, Home, scripts, _dir[".env"],
            0, 3600, 3600, _dir.Path, Path.GetTempPath(), _dir["appdata"]);
        _app = builder.Build();
        _app.MapDashboard();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_app is not null) await _app.DisposeAsync();
        _dir.Dispose();
    }

    private async Task<(int Status, string Type, string Body, string? Cache)> Get(string path)
    {
        using var r = await _http.GetAsync(path, TestContext.Current.CancellationToken);
        return ((int)r.StatusCode, r.Content.Headers.ContentType?.ToString() ?? "", await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            r.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Any_path_with_dot_dot_is_a_400_before_anything_else()
    {
        Assert.Equal((400, """{"ok":false,"message":"bad path"}"""), ((await Get("/state/a..b")).Status, (await Get("/state/a..b")).Body));
        Assert.Equal(400, (await Get("/api/jobs/J-001%2F..%2Fx/files")).Status);
    }

    [Fact]
    public async Task An_unknown_route_or_a_bad_id_is_a_json_404()
    {
        Assert.Equal((404, "application/json; charset=utf-8", """{"ok":false,"message":"not found"}"""),
            ((await Get("/api/nope")).Status, (await Get("/api/nope")).Type, (await Get("/api/nope")).Body));
        Assert.Equal(404, (await Get("/api/jobs/not-a-job/files")).Status);
        using var post = await _http.PostAsync("/api/jobs/J-001/provider", new StringContent("{}"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
    }

    [Fact]
    public async Task State_files_are_served_no_store_as_text_while_a_writer_holds_them_open()
    {
        var log = Path.Combine(Home, "jobs", "J-001", "run-1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await using var writer = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await writer.WriteAsync("{\"type\":\"system\"}\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await writer.FlushAsync(TestContext.Current.CancellationToken);

        var r = await Get("/state/jobs/J-001/run-1.jsonl");

        Assert.Equal((200, "text/plain; charset=utf-8", "{\"type\":\"system\"}\n", "no-store"), r);
        Assert.Equal(404, (await Get("/state/jobs/J-001/missing.md")).Status);
    }

    [Fact]
    public async Task The_index_is_the_dashboard_page()
    {
        var r = await Get("/");
        Assert.Equal((200, "text/html; charset=utf-8"), (r.Status, r.Type));
        Assert.Contains("<html", r.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_job_files_list_reads_the_disk()
    {
        Directory.CreateDirectory(Path.Combine(Home, "jobs", "J-002", "round-1"));
        await File.WriteAllTextAsync(Path.Combine(Home, "jobs", "J-002", "round-1", "build.md"), "b", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(Home, "jobs", "J-002", "spec.md"), "s", TestContext.Current.CancellationToken);

        Assert.Equal("""["round-1/build.md","spec.md"]""", (await Get("/api/jobs/J-002/files")).Body);
    }

    [Fact]
    public void Serve_lock_refuses_a_live_holder_names_its_url_and_takes_over_a_dead_ones_lock()
    {
        var path = Path.Combine(Home, "serve.lock");
        File.WriteAllText(path, $"{Environment.ProcessId}\nhttp://localhost:1234/\n");
        var probe = new ProcessProbe();

        var refused = new ServeLock(Home, probe).TryTake();
        Assert.Equal((false, Environment.ProcessId.ToString(), "http://localhost:1234/"), refused);

        using var dead = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
        dead.WaitForExit();
        File.WriteAllText(path, $"{dead.Id}\n");
        var l = new ServeLock(Home, probe);
        Assert.True(l.TryTake().Taken);
        l.Write("http://localhost:5678/");
        Assert.Equal([Environment.ProcessId.ToString(), "http://localhost:5678/"], File.ReadAllLines(path));

        l.Release();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Releasing_leaves_a_lock_another_process_took()
    {
        var path = Path.Combine(Home, "serve.lock");
        File.WriteAllText(path, "1\n");

        new ServeLock(Home, new ProcessProbe()).Release();

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Ideate_config_reads_the_env_file_on_each_request()
    {
        Assert.False((bool)JsonNode.Parse((await Get("/api/ideate/config")).Body)!["enabled"]!);

        await File.WriteAllTextAsync(_dir[".env"], "ANTHROPIC_API_KEY=sk-x\n", TestContext.Current.CancellationToken);

        Assert.True((bool)JsonNode.Parse((await Get("/api/ideate/config")).Body)!["enabled"]!);
    }
}
