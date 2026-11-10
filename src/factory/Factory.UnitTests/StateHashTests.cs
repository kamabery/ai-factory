using Factory.Store;

namespace Factory.UnitTests;

public class StateHashTests
{
    // Computed with factory.ps1's expression: SHA-256 of the lower-cased home, first 16 hex digits.
    [Fact]
    public void Matches_the_hash_factory_ps1_names_its_mutex_and_test_containers_with() =>
        Assert.Equal("5FC3B337C80872C7", StateHash.Of(@"C:\Work\Repo\.factory-runs"));

    [Fact]
    public void Ignores_case() => Assert.Equal(StateHash.Of(@"c:\work\repo\.FACTORY-runs"), StateHash.Of(@"C:\Work\Repo\.factory-runs"));
}
