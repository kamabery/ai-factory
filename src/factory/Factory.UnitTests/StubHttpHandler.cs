using System.Net;

namespace Factory.UnitTests;

/// <summary>Answers every request with 200 and the text <see cref="Respond"/> builds, counting the calls.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, string> Respond { get; set; } = _ => "{}";
    public int Calls { get; private set; }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Respond(request)) };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));
}
