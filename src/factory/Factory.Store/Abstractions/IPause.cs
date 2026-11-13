namespace Factory.Store.Abstractions;

/// <summary>Waiting between polls (a GitHub run, an HCP destroy, a smoke url, a sign-in retry); tests pass time instead.</summary>
public interface IPause
{
    void Sleep(TimeSpan time);
}
