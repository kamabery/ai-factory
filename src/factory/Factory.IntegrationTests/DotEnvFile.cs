using Factory.Store.Abstractions;

namespace Factory.IntegrationTests;

/// <summary>A .env file's KEY=VALUE lines, for a test that calls one adapter outside the composition.</summary>
public sealed class DotEnvFile(string path) : IDotEnv
{
    public string? Get(string name) =>
        File.ReadAllLines(path).Select(l => l.Split('=', 2)).Where(p => p.Length == 2 && p[0].Trim() == name)
            .Select(p => p[1].Trim()).FirstOrDefault(v => v.Length > 0);
}
