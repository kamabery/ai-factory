namespace Factory.Core.Abstractions;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
