namespace Factory.Store.Abstractions;

/// <summary>An HCP Terraform run: its id and status (<c>applied</c>, <c>errored</c>, <c>planned_and_finished</c>, ...).</summary>
public sealed record HcpRun(string Id, string Status)
{
    /// <summary>The statuses a run does not leave.</summary>
    public static readonly string[] Final = ["applied", "errored", "canceled", "discarded", "force_canceled", "planned_and_finished"];

    public bool IsFinal => Core.PsJsonReader.In(Status, Final);
}
