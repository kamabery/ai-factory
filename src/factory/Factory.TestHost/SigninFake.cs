using System.Globalization;
using System.Text.Json.Nodes;

namespace Factory.TestHost;

/// <summary>
/// <c>signin [--log &lt;file&gt;] [--exit &lt;n&gt;] [--lag &lt;n&gt;] -- &lt;args&gt;</c>: stands in for signin-smoke.mjs
/// (FACTORY_SIGNIN_CMD; it was signin-stub.ps1). Appends { args, stdin } to the log (FACTORY_SIGNIN_LOG), test passwords
/// only, and prints the steps a real smoke prints, the typed password among them so a test sees the factory mask it. It
/// exits the exit code (FACTORY_SIGNIN_EXIT, default 0), a failure printing an Entra error first; the first lag calls
/// (FACTORY_SIGNIN_LAG) fail as a fresh Entra app does until it replicates (J-025).
/// </summary>
public static class SigninFake
{
    public static int Run(IReadOnlyList<string> args)
    {
        var a = FakeArgs.Parse(args);
        var stdin = FakeArgs.Stdin().Trim();
        var log = a.Get("log", "FACTORY_SIGNIN_LOG");
        if (log is not null)
            File.AppendAllText(log, new JsonObject { ["args"] = new JsonArray([.. a.Rest.Select(r => (JsonNode?)r)]), ["stdin"] = stdin }.ToJsonString() + "\n");
        var code = int.Parse(a.Get("exit", "FACTORY_SIGNIN_EXIT") ?? "0", CultureInfo.InvariantCulture);
        Console.WriteLine("step: opened the front end");
        Console.WriteLine("step: clicked Sign in");
        if (stdin.Length > 0) Console.WriteLine($"step: typed {stdin}");
        if (a.Get("lag", "FACTORY_SIGNIN_LAG") is { } lag && log is not null && File.ReadAllLines(log).Length <= int.Parse(lag, CultureInfo.InvariantCulture))
        {
            Console.WriteLine("error: AADSTS9002326: Cross-origin token redemption is permitted only for the Single-Page Application client-type.");
            return 1;
        }
        if (code != 0)
        {
            Console.WriteLine("step: identity provider answered with an error");
            Console.WriteLine("url: https://example.ciamlogin.com/oauth2/v2.0/authorize");
            Console.WriteLine("error: AADSTS500011: The resource principal named api://todoapp was not found in the tenant.");
            return code;
        }
        Console.WriteLine("step: signed in: Sign out is showing");
        Console.WriteLine("step: /api/me answered 200");
        return 0;
    }
}
