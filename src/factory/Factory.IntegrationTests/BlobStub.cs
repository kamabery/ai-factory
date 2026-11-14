using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Factory.IntegrationTests;

/// <summary>
/// The Blob REST stub (Factory.TestHost's <see cref="Factory.TestHost.BlobStubApp"/>), in process on a free localhost port.
/// Leases run 20 times faster (a 60 s lease lasts 3 s) and listings page every 2 blobs.
/// </summary>
public sealed class BlobStub : IAsyncLifetime
{
    public const string Token = "test-token";
    private static readonly HttpClient Http = new();
    private Microsoft.AspNetCore.Builder.WebApplication? _app;

    public int Port { get; private set; }

    public async ValueTask InitializeAsync()
    {
        Port = Factory.TestHost.StubHost.FreePort();
        _app = Factory.TestHost.StubHost.Build(Port, new Factory.TestHost.BlobStubApp(Token, pageSize: 2, leaseScale: 0.05).Handle);
        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
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
}
