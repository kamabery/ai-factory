namespace Factory.Cli.Abstractions;

/// <summary>Opens a URL for the human: the system browser, or FACTORY_BROWSER_CMD (a script run with pwsh -File &lt;url&gt;).</summary>
public interface IBrowser
{
    void Open(string url);
}
