using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Api.Extensions;
using Orbit.Api.Middleware;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// End-to-end request-id observability: the id that <see cref="RequestCorrelationMiddleware"/>
/// pins from the inbound <c>X-Orbit-Request-Id</c> header must survive all the way into the error
/// envelopes the exception handlers write, so a single correlation id ties the client, the response
/// header, the JSON body, and the server logs together. The isolated handler contracts live in
/// <see cref="ValidationExceptionHandlerTests"/> / UnhandledExceptionHandlerTests; this file asserts
/// the propagation across the two components on one shared <see cref="HttpContext"/>.
/// </summary>
public class RequestObservabilityMiddlewareTests
{
    private const string IncomingRequestId = "req_client_supplied_42";

    private static async Task<DefaultHttpContext> RunCorrelationAsync()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[HttpContextExtensions.RequestIdHeaderName] = IncomingRequestId;
        context.Response.Body = new MemoryStream();

        var correlation = new RequestCorrelationMiddleware(_ => Task.CompletedTask);
        await correlation.InvokeAsync(context);

        return context;
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task ValidationFailure_PropagatesCorrelationIdIntoGroupedErrorEnvelope()
    {
        var context = await RunCorrelationAsync();
        var exception = new FluentValidation.ValidationException(
        [
            new FluentValidation.Results.ValidationFailure("Email", "Email is required"),
            new FluentValidation.Results.ValidationFailure("Email", "Email is invalid"),
            new FluentValidation.Results.ValidationFailure("Password", "Password is too short")
        ]);

        var handled = await new ValidationExceptionHandler(NullLogger<ValidationExceptionHandler>.Instance)
            .TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().StartWith("application/json");
        context.Response.Headers[HttpContextExtensions.RequestIdHeaderName].ToString()
            .Should().Be(IncomingRequestId);

        var body = await ReadBodyAsync(context);
        body.GetProperty("type").GetString().Should().Be("ValidationFailure");
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("requestId").GetString().Should().Be(IncomingRequestId);

        var errors = body.GetProperty("errors");
        errors.GetProperty("Email").EnumerateArray().Select(m => m.GetString())
            .Should().BeEquivalentTo("Email is required", "Email is invalid");
        errors.GetProperty("Password").EnumerateArray().Select(m => m.GetString())
            .Should().ContainSingle().Which.Should().Be("Password is too short");
    }

    [Fact]
    public async Task UnhandledException_PropagatesCorrelationIdIntoStructured500Envelope()
    {
        var context = await RunCorrelationAsync();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/habits";

        var handled = await new UnhandledExceptionHandler(NullLogger<UnhandledExceptionHandler>.Instance)
            .TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        context.Response.ContentType.Should().StartWith("application/json");
        context.Response.Headers[HttpContextExtensions.RequestIdHeaderName].ToString()
            .Should().Be(IncomingRequestId);

        var body = await ReadBodyAsync(context);
        body.GetProperty("error").GetString().Should().Be("Unexpected server error");
        body.GetProperty("requestId").GetString().Should().Be(IncomingRequestId);
        body.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status500InternalServerError);
    }
}
