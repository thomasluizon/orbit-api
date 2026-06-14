using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Api.RateLimiting;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.RateLimiting;

public class DistributedRateLimitFilterTests
{
    private readonly IDistributedRateLimitService _service = Substitute.For<IDistributedRateLimitService>();
    private readonly ILogger<DistributedRateLimitFilter> _logger = Substitute.For<ILogger<DistributedRateLimitFilter>>();

    [Fact]
    public async Task SupportPolicy_FailsOpen_WhenRateLimitStoreUnavailable()
    {
        _service.TryAcquireAsync("support", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("rate-limit store down"));

        var filter = new DistributedRateLimitFilter("support", _service, _logger);
        var (context, _) = CreateExecutingContext();
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        nextCalled.Should().BeTrue();
        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task NonSupportPolicy_FailsClosed_WhenRateLimitStoreUnavailable()
    {
        _service.TryAcquireAsync("chat", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("rate-limit store down"));

        var filter = new DistributedRateLimitFilter("chat", _service, _logger);
        var (context, _) = CreateExecutingContext();
        var nextCalled = false;

        var act = () => filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task SupportPolicy_StillThrottles_WhenStoreReportsLimitExceeded()
    {
        _service.TryAcquireAsync("support", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 3, 3, DateTime.UtcNow.AddHours(1)));

        var filter = new DistributedRateLimitFilter("support", _service, _logger);
        var (context, _) = CreateExecutingContext();
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(CreateExecutedContext(context));
        });

        nextCalled.Should().BeFalse();
        context.Result.Should().NotBeNull();
    }

    private static (ActionExecutingContext Context, HttpContext HttpContext) CreateExecutingContext()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/support";

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor());

        var executingContext = new ActionExecutingContext(
            actionContext,
            [],
            new Dictionary<string, object?>(),
            controller: new object());

        return (executingContext, httpContext);
    }

    private static ActionExecutedContext CreateExecutedContext(ActionExecutingContext executingContext)
    {
        return new ActionExecutedContext(executingContext, executingContext.Filters, executingContext.Controller);
    }
}
