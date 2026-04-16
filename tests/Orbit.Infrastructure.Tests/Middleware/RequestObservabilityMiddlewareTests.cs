using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Api.Extensions;
using Orbit.Api.Middleware;

namespace Orbit.Infrastructure.Tests.Middleware;

public class RequestObservabilityMiddlewareTests
{
    [Fact]
    public async Task RequestCorrelationMiddleware_UsesIncomingHeaderAndSetsResponseHeader()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HttpContextExtensions.RequestIdHeaderName] = "req_incoming_123";

        var middleware = new RequestCorrelationMiddleware(async context =>
        {
            await Task.CompletedTask;
        });

        await middleware.InvokeAsync(httpContext);
        await httpContext.Response.StartAsync();

        httpContext.TraceIdentifier.Should().Be("req_incoming_123");
        httpContext.Response.Headers[HttpContextExtensions.RequestIdHeaderName].ToString()
            .Should().Be("req_incoming_123");
    }

    [Fact]
    public async Task ValidationExceptionHandler_ReturnsRequestIdInResponse()
    {
        var handler = new ValidationExceptionHandler(NullLogger<ValidationExceptionHandler>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "req_validation_123";
        httpContext.Response.Body = new MemoryStream();
        var exception = new FluentValidation.ValidationException([
            new FluentValidation.Results.ValidationFailure("Email", "Email is required")
        ]);

        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        httpContext.Response.Headers[HttpContextExtensions.RequestIdHeaderName].ToString()
            .Should().Be("req_validation_123");

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
        json.RootElement.GetProperty("requestId").GetString().Should().Be("req_validation_123");
    }

    [Fact]
    public async Task UnhandledExceptionHandler_ReturnsStructured500WithRequestId()
    {
        var handler = new UnhandledExceptionHandler(NullLogger<UnhandledExceptionHandler>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.TraceIdentifier = "req_server_123";
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Path = "/api/auth/send-code";
        httpContext.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("boom"),
            CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        httpContext.Response.Headers[HttpContextExtensions.RequestIdHeaderName].ToString()
            .Should().Be("req_server_123");

        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body);
        using var json = JsonDocument.Parse(await reader.ReadToEndAsync());
        json.RootElement.GetProperty("requestId").GetString().Should().Be("req_server_123");
        json.RootElement.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status500InternalServerError);
    }
}
