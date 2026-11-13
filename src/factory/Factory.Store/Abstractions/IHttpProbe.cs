namespace Factory.Store.Abstractions;

/// <summary>The smoke check's GET (deploy step 7): never throws; a request that fails is status 0 with its error as the body.</summary>
public interface IHttpProbe
{
    ProbeResult Get(string url, TimeSpan timeout);
}
