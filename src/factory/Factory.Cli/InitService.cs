using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// factory init (contract, "Secrets, init and doctor"): tools, the GitHub App (manifest flow, install, sign-in), the LLM,
/// the settings, then doctor, the dashboard and the browser. Each step is skipped when it is already done.
/// </summary>
public sealed class InitService(
    CliEnvironment host, ToolsCheck tools, LlmCheck llmCheck, ILlmProvider llm, ISecrets secrets, ISecretStore store, IGitHubApp app,
    AppManifest manifest, IDotEnv dotEnv, IFactoryPaths paths, IFileStore files, EnvSettings env, IBrowser browser, IFactoryLauncher launcher,
    IProcessRunner processes, IPause pause, TimeProvider clock)
{
    private const string FoundryUser = "53ca6127-db72-4b80-b1b0-d745d6d5456d";   // Foundry User (formerly Azure AI User), by id

    private string Web => (dotEnv.Get("GITHUB_WEB_URL") ?? "https://github.com").TrimEnd('/');

    private TimeSpan Wait => TimeSpan.FromSeconds(int.TryParse(host.Get("FACTORY_INIT_WAIT_SECONDS"), CultureInfo.InvariantCulture, out var s) ? s : 600);

    public int Run(InitOptions o, TextWriter output)
    {
        output.WriteLine("AI factory: setting up this machine.");
        var missing = tools.Missing(o.Foundry is not null);
        if (missing.Count > 0)
        {
            foreach (var t in missing) output.WriteLine($"missing {t}: {InstallHints.For(t, OperatingSystem.IsWindows())}");
            output.WriteLine("Install them, open a new terminal, and run factory init again.");
            return 1;
        }
        output.WriteLine("ok    tools");

        var envPath = paths.EnvFile ?? throw new FactoryException("init: run it inside the factory's checkout (no .env path)");
        env.EnsureFrom(envPath, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(envPath))!, ".env.example"));
        // .env is read once per process: what init writes must be read again before the next step uses it.
        dotEnv.Refresh();
        if (o.MoveEnvSecrets) MoveSecrets(envPath, output);

        var owner = GitHubApp(o, envPath, output);
        if (Llm(o, envPath, output) is { } stop) return stop;

        output.WriteLine();
        var doctor = processes.Run("dotnet", [paths.CliDll, "doctor"], host.WorkingDirectory, null, host.Variables);
        output.Write(doctor.StdOut);
        if (doctor.ExitCode != 0)
        {
            output.WriteLine("factory doctor failed; fix what it says, then run factory init again.");
            return 1;
        }
        var url = Dashboard();
        output.WriteLine($"The dashboard is {url} (GitHub owner {owner}). Start with New idea.");
        if (!o.NoOpen) browser.Open(url);
        return 0;
    }

    private void MoveSecrets(string envPath, TextWriter output)
    {
        foreach (var name in secrets.InEnvFile())
        {
            if (env.CommentOut(envPath, name, "moved to the secret store") is { Length: > 0 } value) store.Set(name, value);
            output.WriteLine($"moved {name} to {store.Name}");
        }
        dotEnv.Refresh();
    }

    // --- the GitHub App ------------------------------------------------------------------------------------------

    private string GitHubApp(InitOptions o, string envPath, TextWriter output)
    {
        var owner = o.Org ?? dotEnv.Get("GITHUB_OWNER");
        if (secrets.Get(SecretNames.AppId) is null) owner = Create(o, owner, output);
        if (owner is null) throw new FactoryException("init: GITHUB_OWNER is not set; run factory init --org <owner>, or set GITHUB_OWNER in .env");
        env.Set(envPath, "GITHUB_OWNER", owner);
        dotEnv.Refresh();
        Install(owner, output);
        if (secrets.Get(SecretNames.UserToken) is null) SignIn(output);
        output.WriteLine($"ok    github: the App {secrets.Get(SecretNames.AppSlug)} on {owner}");
        return owner;
    }

    private string Create(InitOptions o, string? owner, TextWriter output)
    {
        using var server = new LoopbackServer();
        var state = Guid.NewGuid().ToString("N");
        var json = manifest.For(owner ?? Environment.UserName, server.Base).ToJsonString();
        var action = o.Org is null ? $"{Web}/settings/apps/new?state={state}" : $"{Web}/organizations/{o.Org}/settings/apps/new?state={state}";
        server.Page("/start", $"""
            <!doctype html><title>AI factory</title><p>Creating the AI factory's GitHub App on GitHub...</p>
            <form id="f" method="post" action="{WebUtility.HtmlEncode(action)}"><input type="hidden" name="manifest" value="{WebUtility.HtmlEncode(json)}"><noscript><button>Continue to GitHub</button></noscript></form>
            <script>document.getElementById('f').submit()</script>
            """);
        output.WriteLine($"Create the GitHub App: your browser opens {server.Base}/start, then GitHub; click Create GitHub App.");
        browser.Open($"{server.Base}/start");
        var back = server.Wait("/callback", Wait);
        if (back.GetValueOrDefault("state") != state) throw new FactoryException("init refused: the GitHub callback's state does not match this run");
        var reg = app.ConvertManifest(back.GetValueOrDefault("code") ?? throw new FactoryException("init: GitHub sent no code back"));
        store.Set(SecretNames.AppId, reg.Id);
        store.Set(SecretNames.AppSlug, reg.Slug);
        store.Set(SecretNames.AppClientId, reg.ClientId);
        store.Set(SecretNames.AppClientSecret, reg.ClientSecret);
        store.Set(SecretNames.AppPrivateKey, reg.PrivateKey);
        output.WriteLine($"created the GitHub App {reg.Slug}; its keys are in {store.Name}");
        return o.Org ?? reg.Owner;
    }

    private void Install(string owner, TextWriter output)
    {
        AppInstallation? Find() => app.Installations(Jwt()).FirstOrDefault(i => string.Equals(i.Account, owner, StringComparison.OrdinalIgnoreCase));
        var found = Find();
        if (found is { Selection: "all" }) return;
        var url = $"{Web}/apps/{secrets.Get(SecretNames.AppSlug)}/installations/new";
        output.WriteLine($"Install the App on {owner}, on All repositories: {url}");
        browser.Open(url);
        var until = clock.GetUtcNow() + Wait;
        while ((found = Find()) is not { Selection: "all" })
        {
            if (clock.GetUtcNow() >= until)
                throw new FactoryException(found is null
                    ? $"init: the App was not installed on {owner} within {Wait.TotalMinutes:0} minutes; run factory init again"
                    : $"init refused: the App is installed on selected repositories of {owner}; set Repository access to All repositories, then run factory init again");
            pause.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    private void SignIn(TextWriter output)
    {
        using var server = new LoopbackServer();
        var state = Guid.NewGuid().ToString("N");
        var redirect = $"{server.Base}/oauth";
        var clientId = Need(SecretNames.AppClientId);
        var url = $"{Web}/login/oauth/authorize?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}&state={state}";
        output.WriteLine("Sign in to the App, so it can create repos as you: your browser opens GitHub; click Authorize.");
        browser.Open(url);
        var back = server.Wait("/oauth", Wait);
        if (back.GetValueOrDefault("state") != state) throw new FactoryException("init refused: the GitHub sign-in's state does not match this run");
        AppCredential.Keep(store, app.ExchangeCode(clientId, Need(SecretNames.AppClientSecret), back.GetValueOrDefault("code") ?? "", redirect));
    }

    private string Jwt() => GitHubJwt.Create(Need(SecretNames.AppId), Need(SecretNames.AppPrivateKey), clock.GetUtcNow());

    private string Need(string name) => secrets.Get(name) ?? throw new FactoryException($"init: {name} is not in the secret store; run factory init again");

    // --- the LLM -------------------------------------------------------------------------------------------------

    private int? Llm(InitOptions o, string envPath, TextWriter output)
    {
        if (o.Foundry is { } resource)
        {
            Foundry(resource, envPath, output);
            return null;
        }
        IReadOnlyDictionary<string, string?> child;
        try { child = llm.ChildEnvironment(); }
        catch (FactoryException) { return null; }   // cloud mode with no resource: doctor says so
        if (child.GetValueOrDefault("CLAUDE_CODE_USE_FOUNDRY") == "1") return null;
        if (llmCheck.NotSubscribed(child) is { } why)
        {
            output.WriteLine($"{why}: run claude, then /login, choose your claude.ai account, and run factory init again.");
            return 1;
        }
        output.WriteLine("ok    llm: claude is logged in to a claude.ai subscription");
        return null;
    }

    private void Foundry(string resource, string envPath, TextWriter output)
    {
        var id = Az("cognitiveservices", "account", "list", "--query", $"[?name=='{resource}'].id | [0]", "-o", "tsv");
        if (id.Length == 0) throw new FactoryException($"init: no Foundry resource {resource} in this az subscription; provision it with factory/infra/llm (docs/FOUNDRY.md)");
        var me = Az("ad", "signed-in-user", "show", "--query", "id", "-o", "tsv");
        Az("role", "assignment", "create", "--assignee-object-id", me, "--assignee-principal-type", "User", "--role", FoundryUser, "--scope", id, "-o", "none");
        env.Set(envPath, "FACTORY_MODE", "cloud");
        env.Set(envPath, "FACTORY_FOUNDRY_RESOURCE", resource);
        dotEnv.Refresh();
        output.WriteLine($"ok    llm: cloud mode on {resource}, Foundry User for you (it can take a few minutes to apply)");
    }

    private string Az(params string[] args)
    {
        var r = processes.Run(StateToken.AzProgram, args, "");
        if (r.ExitCode != 0) throw new FactoryException($"init: az {args[0]} {args[1]} failed: {(r.StdErr + r.StdOut).Trim()}");
        return r.StdOut.Trim();
    }

    // --- the dashboard -------------------------------------------------------------------------------------------

    // serve.lock holds the running server's PID, then its URL: one is reused, else a server starts on a free port.
    private string Dashboard()
    {
        var held = files.ReadText(Path.Combine(paths.Home, "serve.lock"))?.Split('\n').Select(l => l.Trim()).ToArray();
        if (held is { Length: >= 2 } && int.TryParse(held[0], CultureInfo.InvariantCulture, out var pid) && Alive(pid) && held[1].Length > 0) return held[1];
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        launcher.StartDetached(["serve", "--port", port.ToString(CultureInfo.InvariantCulture)]);
        return $"http://localhost:{port}/";
    }

    private static bool Alive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
