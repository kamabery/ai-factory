using System.Text.Json.Nodes;
using AutoFixture;
using AutoFixture.AutoNSubstitute;
using AutoFixture.Xunit3;

namespace Factory.UnitTests;

/// <summary>Builds the system under test with an NSubstitute substitute for every dependency (docs/TESTING.md).</summary>
public sealed class AutoNSubDataAttribute() : AutoDataAttribute(Create)
{
    internal static IFixture Create()
    {
        var f = new Fixture().Customize(new AutoNSubstituteCustomization { ConfigureMembers = true });
        // JsonObject's constructors take options AutoFixture cannot build; an empty board object will do.
        f.Register(() => new JsonObject());
        return f;
    }
}
