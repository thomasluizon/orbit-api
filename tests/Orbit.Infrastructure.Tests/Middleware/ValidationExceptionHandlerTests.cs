using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Api.Extensions;
using Orbit.Api.Middleware;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// The handler that turns a FluentValidation <see cref="ValidationException"/> into the app's
/// standard 400 envelope: a JSON body of <c>{ type, status, requestId, errors }</c> where
/// <c>errors</c> groups every message under its property name, plus the request-id echo header.
/// </summary>
public class ValidationExceptionHandlerTests
{
    private static ValidationExceptionHandler CreateHandler() =>
        new(NullLogger<ValidationExceptionHandler>.Instance);

    private static DefaultHttpContext CreateContext(string requestId)
    {
        var context = new DefaultHttpContext { TraceIdentifier = requestId };
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task TryHandleAsync_ValidationException_Writes400JsonEnvelopeWithGroupedErrors()
    {
        var context = CreateContext("trace-abc-123");
        var exception = new ValidationException(
        [
            new ValidationFailure("Email", "Email is required"),
            new ValidationFailure("Email", "Email is invalid"),
            new ValidationFailure("Name", "Name is required")
        ]);

        var handled = await CreateHandler().TryHandleAsync(context, exception, CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().StartWith("application/json");
        context.Response.Headers[HttpContextExtensions.RequestIdHeaderName].ToString()
            .Should().Be("trace-abc-123");

        var root = await ReadJsonAsync(context);
        root.GetProperty("type").GetString().Should().Be("ValidationFailure");
        root.GetProperty("status").GetInt32().Should().Be(400);
        root.GetProperty("requestId").GetString().Should().Be("trace-abc-123");

        var errors = root.GetProperty("errors");
        errors.GetProperty("Email").EnumerateArray().Select(m => m.GetString())
            .Should().BeEquivalentTo("Email is required", "Email is invalid");
        errors.GetProperty("Name").EnumerateArray().Select(m => m.GetString())
            .Should().ContainSingle().Which.Should().Be("Name is required");
    }

    [Fact]
    public async Task TryHandleAsync_NonValidationException_ReturnsFalseAndLeavesResponseUntouched()
    {
        var context = CreateContext("trace-xyz");

        var handled = await CreateHandler().TryHandleAsync(
            context, new InvalidOperationException("unrelated"), CancellationToken.None);

        handled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Length.Should().Be(0);
        context.Response.Headers.Should().NotContainKey(HttpContextExtensions.RequestIdHeaderName);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }
}
