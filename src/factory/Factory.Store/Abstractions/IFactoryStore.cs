namespace Factory.Store.Abstractions;

/// <summary>Where the board and events live (contract, "State backends"): local files, or a Blob container they cache.</summary>
public interface IFactoryStore
{
    /// <summary>The hosted seam: always <c>default</c> for now, and never written to board.json.</summary>
    string TenantId { get; }

    /// <summary>
    /// Takes the board lock (the machine's mutex, and in azure the lease on <c>locks/board</c>) and reads the
    /// board, refreshing it from the container first in azure. Refuses with factory.ps1's one-line reasons.
    /// </summary>
    /// <param name="refresh">
    /// In azure, refresh the board from the container first; only state-migrate, whose container has no board yet, does not.
    /// </param>
    IBoardSession Open(bool refresh = true);
}
