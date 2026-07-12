using System.Net;
using System.Text;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// Stub transport that plays back a scripted sequence of outcomes (a status code or a thrown
/// transport exception) across successive sends, so retry behaviour can be asserted via
/// <see cref="CallCount"/>. The final scripted step repeats for any further sends, modelling a
/// persistently failing endpoint.
/// </summary>
internal sealed class SequencedHttpMessageHandler(params Func<HttpResponseMessage>[] steps) : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _steps = new(steps);

    public int CallCount { get; private set; }

    public static Func<HttpResponseMessage> Status(HttpStatusCode status, string body = "{}") =>
        () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    public static Func<HttpResponseMessage> Throws() =>
        () => throw new HttpRequestException("simulated transport failure");

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        var step = _steps.Count > 1 ? _steps.Dequeue() : _steps.Peek();
        await Task.Yield();
        return step();
    }
}
