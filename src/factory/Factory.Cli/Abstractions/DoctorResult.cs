namespace Factory.Cli.Abstractions;

/// <summary>One check's line: its outcome, what it found, and for a warn or a fail what to do.</summary>
public sealed record DoctorResult(DoctorStatus Status, string Detail, string? Fix = null)
{
    public static DoctorResult Ok(string detail) => new(DoctorStatus.Ok, detail);

    public static DoctorResult Warn(string detail, string fix) => new(DoctorStatus.Warn, detail, fix);

    public static DoctorResult Fail(string detail, string fix) => new(DoctorStatus.Fail, detail, fix);
}
