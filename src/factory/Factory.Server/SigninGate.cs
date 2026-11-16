namespace Factory.Server;

/// <summary>
/// serve --require-signin (contract, "Cloud mode (deployed)", Sign-in): every request must carry Easy Auth's
/// X-MS-CLIENT-PRINCIPAL-ID, which Container Apps sets on a signed-in request and strips from any request that brings
/// its own; without it, 401. So the port is never open without Easy Auth in front.
/// </summary>
public sealed class SigninGate(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (string.IsNullOrEmpty(ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"]))
        {
            await ReplyWriter.WriteAsync(ctx, Reply.Error(401, "sign-in required"));
            return;
        }
        await next(ctx);
    }
}
