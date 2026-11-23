using Factory.Server;
using Factory.Server.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>Spec 24: GET /api/platform (cached 30 s), POST /api/platform/start (detached) and /sleep (contract, "Dashboard API").</summary>
public class ServerPlatformTests : ServerTestBase
{
    private PlatformApi Sut => _sut ??= new PlatformApi(Factory, Launcher, Clock);
    private PlatformApi? _sut;

    public ServerPlatformTests() => Factory.Run("platform", "status").Returns(CallResult.Done("asleep\n"));

    [Fact]
    public void It_answers_platform_status_and_asks_again_only_after_30_seconds()
    {
        Assert.Equal((200, """{"ok":true,"message":"asleep"}"""), (Sut.Read().Status, Sut.Read().Body));
        Clock.Advance(TimeSpan.FromSeconds(29));
        Sut.Read();
        Factory.Received(1).Run("platform", "status");

        Clock.Advance(TimeSpan.FromSeconds(2));
        Sut.Read();
        Factory.Received(2).Run("platform", "status");
    }

    [Fact]
    public void A_refusal_such_as_deploys_off_is_answered_and_not_cached()
    {
        Factory.Run("platform", "status").Returns(CallResult.Refuse("platform refused: deploys are off"));

        Assert.Equal((409, """{"ok":false,"message":"platform refused: deploys are off"}"""), (Sut.Read().Status, Sut.Read().Body));
        Factory.Received(2).Run("platform", "status");
    }

    [Fact]
    public void Start_runs_detached_answers_at_once_and_drops_the_cache()
    {
        Sut.Read();

        Assert.Equal((200, """{"ok":true,"message":"starting the platform"}"""), (Sut.Start().Status, Sut.Start().Body));
        Launcher.Received(2).StartDetached(Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "platform", "start" })));
        Sut.Read();
        Factory.Received(2).Run("platform", "status");
    }

    [Fact]
    public void Sleep_answers_platform_sleep_and_drops_the_cache()
    {
        Factory.Run("platform", "sleep").Returns(CallResult.Done("platform: stopping"));
        Sut.Read();

        Assert.Equal(200, Sut.Sleep().Status);
        Sut.Read();
        Factory.Received(2).Run("platform", "status");
    }

    [Fact]
    public void A_refused_sleep_names_what_holds_the_platform()
    {
        Factory.Run("platform", "sleep").Returns(CallResult.Refuse("platform busy: J-039's preview is up"));

        Assert.Equal((409, """{"ok":false,"message":"platform busy: J-039's preview is up"}"""), (Sut.Sleep().Status, Sut.Sleep().Body));
    }
}
