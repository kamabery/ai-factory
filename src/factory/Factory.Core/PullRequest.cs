namespace Factory.Core;

/// <summary>A job's PR as board.json keeps it: <c>{ number, url }</c>.</summary>
public sealed record PullRequest(int Number, string Url);
