using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Api.Middleware;
using Orbit.Application.Common;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// The safety net that turns an unhandled concurrency conflict from a plain edit into a clean 409
/// (instead of the 500 catch-all). Counter/progress handlers retry and never reach this handler.
/// </summary>
public class ConcurrencyExceptionHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_DbUpdateConcurrencyException_Returns409WithConflictBody()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var handler = new ConcurrencyExceptionHandler(NullLogger<ConcurrencyExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context, new DbUpdateConcurrencyException("stale token"), CancellationToken.None);

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        document.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.ConcurrentUpdateConflict);
    }

    [Fact]
    public async Task TryHandleAsync_OtherException_ReturnsFalseToDelegateToNextHandler()
    {
        var context = new DefaultHttpContext();
        var handler = new ConcurrencyExceptionHandler(NullLogger<ConcurrencyExceptionHandler>.Instance);

        var handled = await handler.TryHandleAsync(
            context, new InvalidOperationException("unrelated"), CancellationToken.None);

        handled.Should().BeFalse();
    }
}
