namespace Factory.Store;

/// <summary>ETags compare without the quotes the list XML leaves off and the headers put on.</summary>
public static class ETags
{
    public static string? Bare(string? e) => string.IsNullOrEmpty(e) ? null : e.Trim('"');

    public static string? Quote(string? e) => string.IsNullOrEmpty(e) ? null : $"\"{Bare(e)}\"";
}
