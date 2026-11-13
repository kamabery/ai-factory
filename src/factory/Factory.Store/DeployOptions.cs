using System.Globalization;

namespace Factory.Store;

/// <summary>
/// The deploy and canary settings from the environment (contract, "Deploy" and "Canary"): the overrides tests set, and
/// the waits. A value that is not a number takes its default, as factory.ps1 would refuse to run with one.
/// </summary>
public sealed class DeployOptions
{
    /// <summary>FACTORY_GH_CMD / FACTORY_AZ_CMD: a script run with <c>pwsh -File</c> in place of gh / az.</summary>
    public string? GhCmd { get; set; }
    public string? AzCmd { get; set; }

    /// <summary>FACTORY_SIGNIN_CMD: a script run with <c>pwsh -File</c> in place of signin-smoke.mjs.</summary>
    public string? SigninCmd { get; set; }

    /// <summary>FACTORY_CANARY_SCAFFOLD_CMD: a script handed the scaffold commands, in place of running them.</summary>
    public string? CanaryScaffoldCmd { get; set; }

    public string? PollSeconds { get; set; }
    public string? TimeoutMinutes { get; set; }
    public string? SmokeSeconds { get; set; }
    public string? SigninSeconds { get; set; }
    public string? SigninRetrySeconds { get; set; }
    public string? DestroyStaleMinutes { get; set; }

    /// <summary>FACTORY_DEPLOY_POLL_SECONDS: between looks at a GitHub or HCP run (default 15).</summary>
    public int Poll => Int(PollSeconds, 15);

    /// <summary>FACTORY_DEPLOY_TIMEOUT_MINUTES: how long a deploy run or a destroy is waited on (default 60).</summary>
    public int Timeout => Int(TimeoutMinutes, 60);

    /// <summary>FACTORY_SMOKE_SECONDS: how long each smoke url is retried until it answers 200 (default 300).</summary>
    public int Smoke => Int(SmokeSeconds, 300);

    /// <summary>FACTORY_SIGNIN_SECONDS: the sign-in smoke's own timeout (default 120).</summary>
    public int Signin => Int(SigninSeconds, 120);

    /// <summary>FACTORY_SIGNIN_RETRY_SECONDS: between sign-in tries while Entra replicates a new app (default 30).</summary>
    public int SigninRetry => Int(SigninRetrySeconds, 30);

    /// <summary>FACTORY_DESTROY_STALE_MINUTES: a destroy started longer ago than this has died (default 90).</summary>
    public int DestroyStale => Int(DestroyStaleMinutes, 90);

    private static int Int(string? v, int fallback) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
}
