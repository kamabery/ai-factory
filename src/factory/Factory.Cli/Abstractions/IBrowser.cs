namespace Factory.Cli.Abstractions;

/// <summary>Opens a URL for the human: the system browser, or FACTORY_BROWSER_CMD (a program and its leading arguments, the URL last).</summary>
public interface IBrowser
{
    void Open(string url);
}
