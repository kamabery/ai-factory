using System.Text.Json.Nodes;

namespace Factory.Runner;

/// <summary>
/// The settings every headless claude pass runs with (contract, "Runner"). Agents work in the job's
/// throwaway clone, so the whole toolchain (scaffolding included) is allowed, plus read-only shell commands. Anything
/// that would leave the branch (push, merge, switching to main) is denied, and so is reading .env and the environment:
/// only the factory CLI holds the GitHub PAT. Each rule is given for both the Bash and the PowerShell tool.
/// </summary>
public static class HeadlessSettings
{
    public static readonly string[] AllowCommands =
    [
        "dotnet *", "npm *", "npx *", "node *", "aspire *", "aspire-addon *",
        // Offline Terraform for the devops agent (no backend, so no state and no credentials), and curl for the
        // approver's smoke test of the preview. Never apply, plan or destroy: factory.ps1 deploys.
        "terraform init -backend=false*", "terraform validate*", "terraform test*", "terraform fmt*", "curl *",
        // The same with -chdir, as the devops agent runs them from the work directory; the deny list fences the wildcard.
        "terraform -chdir=* init -backend=false*", "terraform -chdir=* validate*", "terraform -chdir=* test*", "terraform -chdir=* fmt*",
        "git status*", "git diff*", "git log*", "git show*", "git add *", "git commit *", "git branch*", "git rev-parse*", "git ls-files*",
        // Both only add a commit on the job branch; a rework may ask for a revert (J-022).
        "git revert *", "git rm *",
        // The one merge allowed: the job's base into its branch. checkout points factory/base at it (J-022).
        "git merge --no-edit factory/base*", "git merge --abort*",
        "ls*", "cat *", "head *", "tail *", "wc *", "grep *", "echo *", "pwd", "cd *", "which *",
        "Get-ChildItem*", "Get-Content *", "Select-String *", "Test-Path *", "Resolve-Path *", "Get-Location", "Set-Location *", "Write-Output *",
    ];

    /// <summary>The environment may hold credentials the human never meant an agent to read, so reading it is denied too.</summary>
    public static readonly string[] DenyCommands =
    [
        "git push*", "git merge main*", "git merge origin*", "git rebase*", "git reset --hard*", "git checkout main*", "git switch main*", "git branch -D*", "git worktree*",
        "terraform * apply*", "terraform * plan*", "terraform * destroy*",
        "printenv*", "env*", "Get-ChildItem env:*", "Get-Item env:*", "Get-Content env:*",
    ];

    /// <summary>The settings for a factory at <paramref name="factoryRoot"/> with its state in <paramref name="stateDir"/>.</summary>
    public static JsonObject Build(string factoryRoot, string templatesRoot, string stateDir)
    {
        // The CLI, by name: the runner puts this build's first on claude's PATH.
        var allow = new JsonArray([.. Both(AllowCommands), (JsonNode)"Bash(factory *)", (JsonNode)"PowerShell(factory *)"]);
        var deny = new JsonArray([.. Both(DenyCommands),
            (JsonNode)"Edit(/factory/**)", (JsonNode)"Edit(/.claude/**)", (JsonNode)"Edit(/docs/FACTORY-CONTRACT.md)",
            (JsonNode)$"Edit({RulePath(Path.Combine(factoryRoot, "factory"))}/**)",
            (JsonNode)$"Edit({RulePath(Path.Combine(factoryRoot, ".claude"))}/**)",
            (JsonNode)$"Edit({RulePath(Path.Combine(factoryRoot, "docs", "FACTORY-CONTRACT.md"))})",
            (JsonNode)$"Edit({RulePath(stateDir)}/jobs/*/spec.md)",
            (JsonNode)$"Edit({RulePath(templatesRoot)}/**)",
            (JsonNode)$"Read({RulePath(Path.Combine(factoryRoot, ".env"))})"]);
        return new JsonObject
        {
            // A deploy can take an hour, and a shell call is cut off at 10 minutes by default (J-023).
            ["env"] = new JsonObject { ["BASH_MAX_TIMEOUT_MS"] = "5400000" },
            ["permissions"] = new JsonObject
            {
                ["allow"] = allow,
                ["additionalDirectories"] = new JsonArray(Slashes(Path.Combine(factoryRoot, "factory", "templates")), Slashes(templatesRoot)),
                ["deny"] = deny,
            },
        };
    }

    /// <summary>A Claude Code permission path: forward slashes, and '//d/...' for an absolute Windows path.</summary>
    public static string RulePath(string path)
    {
        var p = Slashes(Path.GetFullPath(path));
        if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0])) p = "/" + char.ToLowerInvariant(p[0]) + p[2..];
        return "/" + p;
    }

    private static string Slashes(string path) => path.Replace('\\', '/');

    private static IEnumerable<JsonNode> Both(IEnumerable<string> commands) =>
        commands.SelectMany(c => new JsonNode[] { $"Bash({c})", $"PowerShell({c})" });
}
