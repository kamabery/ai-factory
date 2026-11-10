using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Factory.IntegrationTests;

/// <summary>
/// factory/tests/fixtures/blob-stub.ps1, the Blob REST stand-in the Pester suites use, on a free localhost port.
/// Leases run 20 times faster (a 60 s lease lasts 3 s) and listings page every 2 blobs.
/// </summary>
public sealed class BlobStub : IAsyncLifetime
{
    public const string Token = "test-token";
    private static readonly HttpClient Http = new();
    private Process? _process;

    public int Port { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var psi = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-File", Script(), "-Port", $"{Port}", "-LeaseScale", "0.05", "-PageSize", "2" }) psi.ArgumentList.Add(a);
        _process = Process.Start(psi)!;
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { (await Http.GetAsync($"http://localhost:{Port}/_stub/state")).EnsureSuccessStatusCode(); return; }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline) { await Task.Delay(200); }
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _process?.Kill(true); } catch (InvalidOperationException) { }
        _process?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>A fresh container's URL: containers never share blobs on the stub.</summary>
    public string NewContainer() => $"http://localhost:{Port}/c{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>The blobs in <paramref name="containerUrl"/>: name -> { type, text, leased }.</summary>
    public JsonObject Blobs(string containerUrl)
    {
        var container = new Uri(containerUrl).AbsolutePath.Trim('/') + "/";
        var state = JsonNode.Parse(Http.GetStringAsync($"http://localhost:{Port}/_stub/state").Result)!;
        var blobs = new JsonObject();
        foreach (var (k, v) in state["blobs"]!.AsObject())
            if (k.StartsWith(container, StringComparison.Ordinal)) blobs[k[container.Length..]] = v!.DeepClone();
        return blobs;
    }

    /// <summary>Makes the stub refuse old-version deletes as it does a principal without Storage Blob Data Owner.</summary>
    public void DenyVersionDelete(bool deny) =>
        Http.PutAsync($"http://localhost:{Port}/_stub/deny-version-delete", new StringContent(deny ? "true" : "false", Encoding.UTF8)).Result.EnsureSuccessStatusCode();

    private static string Script()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "factory", "tests", "fixtures", "blob-stub.ps1");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("factory/tests/fixtures/blob-stub.ps1 not found above " + AppContext.BaseDirectory);
    }
}
