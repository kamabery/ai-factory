namespace Factory.Runner.Abstractions;

/// <summary>A factory.ps1 call's exit code, its stdout trimmed, and its stderr lines joined by spaces (runner.ps1's F-Child).</summary>
public sealed record FactoryResult(int Code, string Out, string Err);
