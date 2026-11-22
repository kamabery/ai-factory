using Factory.Server;
using Factory.Server.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>GET /api/canary (contract, "Dashboard API"): <c>canary -Due</c>, cached for 60 s.</summary>
public class ServerCanaryDueTests : ServerTestBase
{
    private const string Due = """{"due":true,"reasons":["templates changed since pass #32"],"typicalMinutes":15}""";

    [Fact]
    public void It_answers_canary_Due_and_asks_again_only_after_60_seconds()
    {
        Factory.Run("canary", "-Due").Returns(CallResult.Done(Due));
        var sut = new CanaryDue(Factory, Clock);

        Assert.Equal((200, Due), (sut.Read().Status, sut.Read().Body));
        Clock.Advance(TimeSpan.FromSeconds(59));
        sut.Read();
        Factory.Received(1).Run("canary", "-Due");

        Clock.Advance(TimeSpan.FromSeconds(2));
        sut.Read();
        Factory.Received(2).Run("canary", "-Due");
    }

    [Fact]
    public void A_refusal_is_answered_and_not_cached()
    {
        Factory.Run("canary", "-Due").Returns(CallResult.Refuse("canary refused: set FACTORY_CANARY_REPO"));
        var sut = new CanaryDue(Factory, Clock);

        Assert.Equal(409, sut.Read().Status);
        sut.Read();
        Factory.Received(2).Run("canary", "-Due");
    }
}
