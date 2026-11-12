namespace Factory.Runner.Abstractions;

/// <summary>claude-run.ps1's Invoke-ClaudeStream: one headless claude pass with the factory plugin, in a job's work dir.</summary>
public interface IClaudeRunner
{
    /// <summary>
    /// Runs <paramref name="prompt"/> in <paramref name="workDir"/> for job <paramref name="jobId"/>, streaming every output line
    /// to <paramref name="log"/> as it arrives. <paramref name="childEnv"/> is set for claude only (null removes a variable);
    /// <paramref name="resumeSession"/>, when not empty, continues that claude session.
    /// </summary>
    ClaudeRun Run(string jobId, string prompt, string workDir, string log, IReadOnlyDictionary<string, string?> childEnv, string resumeSession);
}
