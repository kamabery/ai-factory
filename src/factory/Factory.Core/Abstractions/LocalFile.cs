namespace Factory.Core.Abstractions;

/// <summary>A file's full path, length and last-write time (UTC ticks), as the sync manifest records them.</summary>
public sealed record LocalFile(string Path, long Length, long LastWriteTicks);
