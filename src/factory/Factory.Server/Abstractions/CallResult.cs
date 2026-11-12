namespace Factory.Server.Abstractions;

/// <summary>One factory.ps1 call as the dashboard answers it: <c>{ ok, message }</c>.</summary>
public sealed record CallResult(bool Ok, string Message)
{
    public static CallResult Refuse(string message) => new(false, message);

    public static CallResult Done(string message) => new(true, message);
}
