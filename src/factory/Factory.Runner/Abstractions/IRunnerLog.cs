namespace Factory.Runner.Abstractions;

/// <summary>runner.log: the runner runs in a hidden window, so this is the only place its failures can be seen.</summary>
public interface IRunnerLog
{
    void Write(string message);
}
