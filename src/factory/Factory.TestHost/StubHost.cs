using System.Text;

namespace Factory.TestHost;

/// <summary>A stub on localhost:<c>port</c>, one request at a time, as the PowerShell stubs were.</summary>
public static class StubHost
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static WebApplication Build(int port, Func<HttpContext, Task<StubReply>> handle)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));
        var app = builder.Build();
        var gate = new SemaphoreSlim(1, 1);
        app.Run(async ctx =>
        {
            StubReply r;
            await gate.WaitAsync();
            try { r = await handle(ctx); }
            catch (Exception e) when (e is not OperationCanceledException) { r = StubReply.Message(500, e.Message); }
            finally { gate.Release(); }
            await Write(ctx, r);
        });
        return app;
    }

    /// <summary>A free localhost port. The probe frees it before the stub binds it.</summary>
    public static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static async Task<string> BodyText(HttpContext ctx)
    {
        using var r = new StreamReader(ctx.Request.Body, Utf8);
        return await r.ReadToEndAsync();
    }

    public static async Task<byte[]> BodyBytes(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task Write(HttpContext ctx, StubReply r)
    {
        if (ctx.Response.HasStarted) return;   // a stub that streamed its own answer (the Messages API's events)
        ctx.Response.StatusCode = r.Status;
        foreach (var (k, v) in r.Headers ?? new Dictionary<string, string>()) ctx.Response.Headers[k] = v;
        byte[]? bytes = r.Bytes ?? (r.Body is not null ? Utf8.GetBytes(r.Body.ToJsonString()) : r.Text is not null ? Utf8.GetBytes(r.Text) : null);
        if (bytes is null || r.Status == 304) return;
        ctx.Response.ContentType = r.Body is not null ? "application/json" : r.ContentType;
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes);
    }
}
