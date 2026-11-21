using Factory.Server;
using Microsoft.AspNetCore.Http;

namespace Factory.UnitTests;

/// <summary>serve --require-signin (contract, "Cloud mode (deployed)", Sign-in): Easy Auth's header, or 401.</summary>
public class SigninGateTests
{
    private static async Task<(bool Next, int Status, string Body)> Send(string? principal)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        if (principal is not null) ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = principal;
        var next = false;

        await new SigninGate(_ => { next = true; return Task.CompletedTask; }).InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        return (next, ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_request_Easy_Auth_signed_in_goes_through()
    {
        Assert.Equal((true, 200, ""), await Send("00000000-0000-0000-0000-000000000001"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_request_without_a_principal_is_refused_401(string? principal)
    {
        Assert.Equal((false, 401, """{"ok":false,"message":"sign-in required"}"""), await Send(principal));
    }
}
