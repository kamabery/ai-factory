using Factory.Cli.Abstractions;

namespace Factory.Cli;

/// <summary>factory doctor's report: <c>ok|warn|fail  &lt;check&gt;: &lt;detail&gt;</c>, a fix line under each warn and fail.</summary>
public sealed class Doctor(IEnumerable<IDoctorCheck> checks) : IDoctor
{
    public bool Run(TextWriter output)
    {
        var ok = true;
        foreach (var c in checks)
        {
            DoctorResult? r;
            try { r = c.Check(); }
            catch (Exception e) { r = DoctorResult.Fail($"the check itself failed: {e.Message}", "report it"); }
            if (r is null) continue;
            output.WriteLine($"{Label(r.Status),-4}  {c.Name}: {r.Detail}");
            if (r.Fix is not null) output.WriteLine($"      fix: {r.Fix}");
            ok &= r.Status != DoctorStatus.Fail;
        }
        return ok;
    }

    private static string Label(DoctorStatus s) => s switch { DoctorStatus.Ok => "ok", DoctorStatus.Warn => "warn", _ => "fail" };
}
