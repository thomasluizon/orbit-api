using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Api.Middleware;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// The catch-all 500 handler, with a short-circuit that treats a client-aborted request
/// (OperationCanceledException while RequestAborted is signalled) as a 499 no-op rather than a
/// reportable server error — https://thomasluizon.sentry.io/issues/ORBIT-API-8
/// </summary>
public class UnhandledExceptionHandlerTests
{
    private const int ClientClosedRequestStatusCode = 499;

    [Fact]
    public async Task TryHandleAsync_ClientAbortedCancellation_Returns499WithoutErrorBody()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var context = new DefaultHttpContext { RequestAborted = aborted.Token };
        context.Response.Body = new MemoryStream();
        var handler = new UnhandledExceptionHandler(NullLogger<UnhandledExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context, new OperationCanceledException(aborted.Token), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(ClientClosedRequestStatusCode);
        context.Response.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task TryHandleAsync_CancellationWithoutClientAbort_FallsThroughTo500()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new UnhandledExceptionHandler(NullLogger<UnhandledExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context, new OperationCanceledException(), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task TryHandleAsync_RegularException_Returns500()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new UnhandledExceptionHandler(NullLogger<UnhandledExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException("boom"), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }
}
