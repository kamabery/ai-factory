namespace Factory.Store.Abstractions;

/// <summary>
/// The external processes the deploy path runs (deploy.ps1's Invoke-Tool, Invoke-SigninSmoke and canary.ps1's Invoke-In):
/// gh, az, the sign-in smoke and the canary's scaffold, each replaceable by a script for tests. Secrets reach a child on
/// stdin or in its own environment, never as an argument.
/// </summary>
public interface IToolRunner
{
    /// <summary><c>gh</c>, or FACTORY_GH_CMD; <paramref name="environment"/> is set for that one child.</summary>
    ToolResult Gh(IReadOnlyList<string> args, string? stdin = null, IReadOnlyDictionary<string, string?>? environment = null);

    /// <summary><c>az</c>, or FACTORY_AZ_CMD, under the human's own <c>az login</c> (the home tenant).</summary>
    ToolResult Az(IReadOnlyList<string> args);

    /// <summary><c>node factory/scripts/signin-smoke.mjs</c>, or FACTORY_SIGNIN_CMD, the password on stdin.</summary>
    ToolResult Signin(IReadOnlyList<string> args, string password);

    /// <summary><c>pwsh -NoProfile &lt;args&gt;</c> in <paramref name="directory"/>: one of the canary's scaffold commands.</summary>
    ToolResult Pwsh(string directory, IReadOnlyList<string> args);
}
