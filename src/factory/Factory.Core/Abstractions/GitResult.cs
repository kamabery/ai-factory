namespace Factory.Core.Abstractions;

/// <summary>A git call's exit code and its output (stdout, then stderr), trimmed.</summary>
public sealed record GitResult(int Code, string Out);
