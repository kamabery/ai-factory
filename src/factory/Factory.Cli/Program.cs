using System.Collections;
using System.Text;
using Factory.Cli;

// UTF-8 without a BOM, whatever the console's code page: factory.ps1 reads it back as UTF-8.
var utf8 = new UTF8Encoding(false);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value, StringComparer.OrdinalIgnoreCase);
using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
// How to start this CLI again (the idea turn's MCP server): the apphost itself, or dotnet with this DLL.
var exe = Environment.ProcessPath ?? "dotnet";
IReadOnlyList<string> self = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
    ? [exe, typeof(CliHost).Assembly.Location]
    : [exe];
return CliHost.Run(args, env, Environment.CurrentDirectory, stdout, stderr, stdin, self);
