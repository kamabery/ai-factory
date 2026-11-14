namespace Factory.Data.Abstractions;

/// <summary>What a save writes: the board row's new skeleton when the board's own keys changed, and the rows that changed.</summary>
public sealed record BoardPlan(string Skeleton, bool SkeletonChanged, IReadOnlyList<RowChange> Rows);
