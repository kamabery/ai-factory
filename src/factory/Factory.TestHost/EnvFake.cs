namespace Factory.TestHost;

/// <summary><c>env &lt;name&gt;...</c>: prints <c>name=[value]</c> for each variable, and <c>cwd=[dir]</c>, so a test sees what a child got.</summary>
public static class EnvFake
{
    public static int Run(IReadOnlyList<string> names)
    {
        foreach (var n in names) Console.WriteLine($"{n}=[{Environment.GetEnvironmentVariable(n)}]");
        Console.WriteLine($"cwd=[{Environment.CurrentDirectory}]");
        return 0;
    }
}
