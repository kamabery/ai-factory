using Factory.Store;

namespace Factory.UnitTests;

public class EnvFileTests
{
    [Fact]
    public void Reads_names_and_values_as_env_file_ps1_does()
    {
        var vars = EnvFile.Parse("""
            # a comment
            FACTORY_STATE=azure
            export FACTORY_BLOB_URL = "https://a.blob.core.windows.net/factory"
            QUOTED='single'
            # NOT=read
              SPACED  =  padded value
            bad line
            1BAD=x
            """);

        Assert.Equal("azure", vars["FACTORY_STATE"]);
        Assert.Equal("https://a.blob.core.windows.net/factory", vars["factory_blob_url"]);
        Assert.Equal("single", vars["QUOTED"]);
        Assert.Equal("padded value", vars["SPACED"]);
        Assert.Equal(4, vars.Count);
    }

    [Fact]
    public void No_file_is_no_values() => Assert.Empty(EnvFile.Parse(null));
}
