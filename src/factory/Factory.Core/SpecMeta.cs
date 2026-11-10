namespace Factory.Core;

public sealed record SpecMeta(string? Repo, IReadOnlyList<GateCommand> Gate);
