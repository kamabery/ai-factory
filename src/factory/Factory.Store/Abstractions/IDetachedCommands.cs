namespace Factory.Store.Abstractions;

/// <summary>The factory commands still running detached in this container (contract, "Cloud mode (deployed)").</summary>
public interface IDetachedCommands
{
    /// <summary>True while a process other than this one runs factory.dll.</summary>
    bool AnyRunning();
}
