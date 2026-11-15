using System.Globalization;
using Factory.Simulator;

// dotnet Factory.Simulator.dll [--speed <x>] [--reset] [--state-dir <dir>]: the simulated run (see Simulation).
// The state dir defaults to <repo>/.factory-runs-sim; --reset deletes it first.
var speed = 1.0;
var reset = false;
string? stateDir = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--speed" when i + 1 < args.Length: speed = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--reset": reset = true; break;
        case "--state-dir" when i + 1 < args.Length: stateDir = args[++i]; break;
        default:
            Console.Error.WriteLine("usage: Factory.Simulator [--speed <x>] [--reset] [--state-dir <dir>]");
            return 2;
    }
}
var repo = Simulation.FindRepoRoot(AppContext.BaseDirectory);
stateDir = Path.GetFullPath(stateDir ?? Path.Combine(repo, ".factory-runs-sim"));
try
{
    if (reset) Simulation.Reset(stateDir);
    await new Simulation(repo, stateDir, speed, Console.Out).Run();
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"simulate: {e.Message}");
    return 1;
}
