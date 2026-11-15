using System.Globalization;
using Factory.TestHost;

// dotnet Factory.TestHost.dll <github|blob|anthropic> --port <n> [options]: one stub until it is stopped.
// dotnet Factory.TestHost.dll <tool|signin|claude|env|launch> [options --] <args>: one fake call (ToolFake, SigninFake, ClaudeFake, EnvFake, LaunchFake).
//   github    --git-base <dir> [--login <user>]
//   blob      [--token <t>] [--log <file>] [--page-size <n>] [--lease-scale <x>]
//   anthropic --dir <dir> [--delay-ms <n>]
// Factory.TestHost -NoProfile -Command <line>: a stand-in gate shell (FACTORY_GATE_SHELL) that prints what it was given.
if (args is ["-NoProfile", ..])
{
    Console.WriteLine($"shell: {string.Join('|', args)}");
    return 0;
}
// The fakes a test override names in place of a program (contract, "Implementations"): Factory.TestHost <mode> [options --] <args>.
switch (args.Length > 0 ? args[0] : "")
{
    case "tool": return ToolFake.Run(args[1..]);
    case "signin": return SigninFake.Run(args[1..]);
    case "claude": return ClaudeFake.Run(args[1..]);
    case "env": return EnvFake.Run(args[1..]);
    case "launch": return LaunchFake.Run(args[1..]);
}
var stub = args.Length > 0 ? args[0] : "";
var opts = new ConfigurationBuilder().AddCommandLine([.. args.Skip(1)]).Build();
if (!int.TryParse(opts["port"], out var port))
{
    Console.Error.WriteLine("usage: Factory.TestHost <github|blob|anthropic> --port <n> [options]");
    return 2;
}
Func<HttpContext, Task<StubReply>> handle;
switch (stub)
{
    case "github":
        var gitBase = Path.GetFullPath(opts["git-base"] ?? throw new ArgumentException("github needs --git-base"));
        handle = new GitHubStubApp(gitBase, opts["login"] ?? "test", port).Handle;
        Console.WriteLine($"github stub on http://localhost:{port}/ (repos: {gitBase})");
        break;
    case "blob":
        handle = new BlobStubApp(opts["token"] ?? "test-token", opts["log"], int.TryParse(opts["page-size"], out var ps) ? ps : 1000,
            double.TryParse(opts["lease-scale"], CultureInfo.InvariantCulture, out var ls) ? ls : 1.0).Handle;
        Console.WriteLine($"blob stub on http://localhost:{port}/");
        break;
    case "anthropic":
        var dir = Path.GetFullPath(opts["dir"] ?? throw new ArgumentException("anthropic needs --dir"));
        handle = new AnthropicStubApp(dir, int.TryParse(opts["delay-ms"], out var d) ? d : 0).Handle;
        Console.WriteLine($"anthropic stub on http://localhost:{port}/ (scripts: {dir})");
        break;
    default:
        Console.Error.WriteLine($"unknown stub '{stub}': github, blob or anthropic");
        return 2;
}
await StubHost.Build(port, handle).RunAsync();
return 0;
