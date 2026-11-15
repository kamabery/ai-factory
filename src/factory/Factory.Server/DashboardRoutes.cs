using System.Text;

namespace Factory.Server;

/// <summary>The contract's "Dashboard API" table, mapped. Anything else is a 404 with <c>{ ok, message }</c>.</summary>
public static class DashboardRoutes
{
    private const string Job = "{id:regex(^[A-Z]+-\\d+$)}";
    private const string Session = "{sid:regex(^S-\\d+$)}";

    // serve.ps1 answered one request at a time. Session writes read, change and write session.json, so the API keeps
    // that order; static reads (the board poll, run logs) are served alongside.
    private static readonly SemaphoreSlim ApiGate = new(1, 1);

    public static WebApplication MapDashboard(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (Uri.UnescapeDataString(ctx.Request.Path.Value ?? "").Contains("..", StringComparison.Ordinal))
            {
                await ReplyWriter.WriteAsync(ctx, Reply.BadPath());
                return;
            }
            var api = ctx.Request.Path.StartsWithSegments("/api");
            if (api) await ApiGate.WaitAsync(ctx.RequestAborted);
            try { await next(ctx); }
            catch (Exception e) when (!ctx.Response.HasStarted)
            {
                app.Logger.LogWarning("{Method} {Path}: {Message}", ctx.Request.Method, ctx.Request.Path, e.Message);
                await ReplyWriter.WriteAsync(ctx, Reply.Error(500, e.Message));
            }
            finally { if (api) ApiGate.Release(); }
        });

        Get(app, "/", (StaticFiles f) => f.Index());
        Get(app, "/index.html", (StaticFiles f) => f.Index());
        Get(app, "/state/{**rest}", (StaticFiles f, string rest) => f.State(rest));
        Get(app, "/assets/{**rest}", (StaticFiles f, string rest) => f.Assets(rest));
        Get(app, "/vendor/{**rest}", (StaticFiles f, string rest) => f.Vendor(rest));

        Get(app, "/api/agents/{agentId:regex(^[A-Za-z0-9_-]+$)}/transcript", (JobsApi j, string agentId) => j.Transcript(agentId));
        Get(app, $"/api/jobs/{Job}/files", (JobsApi j, string id) => j.Files(id));
        Map(app, "PUT", $"/api/jobs/{Job}/spec", (JobsApi j, string id, string body) => j.EditSpec(id, body));
        Map(app, "POST", $"/api/jobs/{Job}/move", (JobsApi j, string id, string body) => j.Move(id, body));
        Map(app, "POST", $"/api/jobs/{Job}/resume", (JobsApi j, string id, string _) => j.Resume(id));
        Map(app, "POST", $"/api/jobs/{Job}/redeploy", (JobsApi j, string id, string _) => j.Redeploy(id));
        Map(app, "POST", $"/api/jobs/{Job}/destroy", (JobsApi j, string id, string _) => j.Destroy(id));
        Get(app, $"/api/jobs/{Job}/qa-login", (JobsApi j, string id) => j.QaLogin(id));
        app.MapPost("/api/canary", (HttpContext ctx, JobsApi j) => ReplyWriter.WriteAsync(ctx, j.Canary()));

        Get(app, "/api/lessons", (ReadsApi r) => r.Lessons());
        Map(app, "POST", $"/api/lessons/{Job}/mark", (ReadsApi r, string id, string body) => r.MarkLesson(id, body));
        Get(app, "/api/evals", (ReadsApi r) => r.Evals());
        Map(app, "PUT", $"/api/evals/{Job}/feedback", (ReadsApi r, string id, string body) => r.Feedback(id, body));
        Get(app, "/api/ideate/config", (ReadsApi r) => r.IdeateConfig());
        Get(app, "/api/repos", (ReadsApi r) => r.Repos());
        Get(app, "/api/sessions", (ReadsApi r) => r.Sessions());
        app.MapPost("/api/purge", (HttpContext ctx, ReadsApi r) => ReplyWriter.WriteAsync(ctx, r.Purge()));
        Map(app, "POST", "/api/ideas/{id:regex(^I-\\d+$)}/abandon", (ReadsApi r, string id, string _) => r.AbandonIdea(id));

        app.MapPost("/api/sessions", async (HttpContext ctx, IdeaSessions s) => await ReplyWriter.WriteAsync(ctx, s.New(await Body(ctx))));
        app.MapPost($"/api/sessions/{Session}/{{action:regex(^(reply|brief|write-specs|close)$)}}",
            async (HttpContext ctx, IdeaSessions s, string sid, string action) => await ReplyWriter.WriteAsync(ctx, s.Handle("POST", sid, action, null, await Body(ctx))));
        app.MapMethods($"/api/sessions/{Session}/specs/{{k:regex(^\\d+$)}}", ["POST", "PUT"],
            async (HttpContext ctx, IdeaSessions s, string sid, string k) => await ReplyWriter.WriteAsync(ctx, s.Handle(ctx.Request.Method, sid, "specs", k, await Body(ctx))));

        app.MapFallback((HttpContext ctx) => ReplyWriter.WriteAsync(ctx, Reply.NotFound()));
        return app;
    }

    private static void Get<T>(WebApplication app, string pattern, Func<T, Reply> handler) where T : notnull =>
        app.MapGet(pattern, (HttpContext ctx, T svc) => ReplyWriter.WriteAsync(ctx, handler(svc)));

    private static void Get<T>(WebApplication app, string pattern, Func<T, string, Reply> handler) where T : notnull =>
        app.MapGet(pattern, (HttpContext ctx, T svc) => ReplyWriter.WriteAsync(ctx, handler(svc, RouteValue(ctx))));

    private static void Map<T>(WebApplication app, string method, string pattern, Func<T, string, string, Reply> handler) where T : notnull =>
        app.MapMethods(pattern, [method], async (HttpContext ctx, T svc) => await ReplyWriter.WriteAsync(ctx, handler(svc, RouteValue(ctx), await Body(ctx))));

    /// <summary>The pattern's one route value (id, agentId or rest).</summary>
    private static string RouteValue(HttpContext ctx) =>
        ctx.Request.RouteValues.Values.OfType<string>().FirstOrDefault() ?? "";

    private static async Task<string> Body(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(ctx.RequestAborted);
    }
}
