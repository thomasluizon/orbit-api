using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Orbit.Api.Extensions;
using Orbit.Api.Middleware;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// <see cref="RequestCorrelationMiddleware"/> pins a per-request correlation id: a safe inbound
/// <c>X-Orbit-Request-Id</c> is trusted and reused, anything unsafe or absent falls back to the
/// framework-generated <see cref="HttpContext.TraceIdentifier"/>, and whichever id wins is echoed on
/// the response header so <see cref="HttpContext.TraceIdentifier"/> (the ambient log-scope id) and the
/// response header always agree — no unvalidated client value ever reaches the logs or the response.
/// </summary>
public class RequestCorrelationMiddlewareTests
{
    private static async Task<(DefaultHttpContext Context, bool NextCalled)> InvokeAsync(
        Action<DefaultHttpContext> configureRequest)
    {
        var context = new DefaultHttpContext();
        configureRequest(context);

        var nextCalled = false;
        var middleware = new RequestCorrelationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);
        return (context, nextCalled);
    }

    private static string ResponseRequestId(HttpContext context) =>
        context.Response.Headers[HttpContextExtensions.RequestIdHeaderName].ToString();

    [Fact]
    public async Task InvokeAsync_ValidIncomingId_TrustsItAndEchoesOnResponse()
    {
        var (context, nextCalled) = await InvokeAsync(c =>
            c.Request.Headers[HttpContextExtensions.RequestIdHeaderName] = "req_incoming_123");

        nextCalled.Should().BeTrue();
        context.TraceIdentifier.Should().Be("req_incoming_123");
        ResponseRequestId(context).Should().Be("req_incoming_123");
    }

    [Fact]
    public async Task InvokeAsync_IncomingIdWithSurroundingWhitespace_IsTrimmedBeforeTrusting()
    {
        var (context, _) = await InvokeAsync(c =>
            c.Request.Headers[HttpContextExtensions.RequestIdHeaderName] = "  req_padded_7  ");

        context.TraceIdentifier.Should().Be("req_padded_7");
        ResponseRequestId(context).Should().Be("req_padded_7");
    }

    [Fact]
    public async Task InvokeAsync_NoIncomingId_KeepsGeneratedTraceIdentifierAndEchoesIt()
    {
        var (context, nextCalled) = await InvokeAsync(_ => { });

        nextCalled.Should().BeTrue();
        context.TraceIdentifier.Should().NotBeNullOrWhiteSpace();
        ResponseRequestId(context).Should().Be(context.TraceIdentifier);
    }

    [Fact]
    public async Task InvokeAsync_WhitespaceOnlyIncomingId_FallsBackToGeneratedId()
    {
        var (context, _) = await InvokeAsync(c =>
            c.Request.Headers[HttpContextExtensions.RequestIdHeaderName] = "   ");

        context.TraceIdentifier.Should().NotBeNullOrWhiteSpace();
        context.TraceIdentifier.Should().NotBe("   ");
        ResponseRequestId(context).Should().Be(context.TraceIdentifier);
    }

    [Fact]
    public async Task InvokeAsync_IncomingIdWithCrlfControlCharacters_IsRejectedForGeneratedId()
    {
        const string injected = "req\r\ninjected-header";

        var (context, _) = await InvokeAsync(c =>
            c.Request.Headers[HttpContextExtensions.RequestIdHeaderName] = injected);

        context.TraceIdentifier.Should().NotBe(injected);
        context.TraceIdentifier.Should().NotBeNullOrWhiteSpace();
        var echoed = ResponseRequestId(context);
        echoed.Should().Be(context.TraceIdentifier);
        echoed.Should().NotContain("\r").And.NotContain("\n");
    }

    [Fact]
    public async Task InvokeAsync_OverlongIncomingId_IsRejectedForGeneratedId()
    {
        var overlong = new string('a', 129);

        var (context, _) = await InvokeAsync(c =>
            c.Request.Headers[HttpContextExtensions.RequestIdHeaderName] = overlong);

        context.TraceIdentifier.Should().NotBe(overlong);
        context.TraceIdentifier.Should().NotBeNullOrWhiteSpace();
        ResponseRequestId(context).Should().Be(context.TraceIdentifier);
    }

    [Fact]
    public async Task InvokeAsync_MaximumLengthIncomingId_IsAccepted()
    {
        var maxLength = new string('b', 128);

        var (context, _) = await InvokeAsync(c =>
            c.Request.Headers[HttpContextExtensions.RequestIdHeaderName] = maxLength);

        context.TraceIdentifier.Should().Be(maxLength);
        ResponseRequestId(context).Should().Be(maxLength);
    }
}
