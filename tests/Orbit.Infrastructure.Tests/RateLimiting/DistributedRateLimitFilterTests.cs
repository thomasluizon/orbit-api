using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Security.Claims;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.RateLimiting;

public class DistributedRateLimitFilterTests
{
    private readonly IDistributedRateLimitService _service = Substitute.For<IDistributedRateLimitService>();
    private readonly IAuthSessionService _authSessionService = Substitute.For<IAuthSessionService>();
    private readonly ILogger<DistributedRateLimitFilter> _logger = Substitute.For<ILogger<DistributedRateLimitFilter>>();

    [Fact]
    public async Task SupportPolicy_FailsOpen_WhenRateLimitStoreUnavailable()
    {
        _service.TryAcquireAsync("support", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("rate-limit store down"));

        var filter = new DistributedRateLimitFilter("support", _service, _authSessionService, _logger);
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

        var filter = new DistributedRateLimitFilter("chat", _service, _authSessionService, _logger);
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

        var filter = new DistributedRateLimitFilter("support", _service, _authSessionService, _logger);
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

    [Fact]
    public async Task RefreshPolicy_PartitionsUnauthenticatedRequestByRefreshToken_WhenTokenMapsToRealSession()
    {
        var refreshToken = new string('A', RefreshTokenRules.TokenLength);
        _authSessionService.HasSessionForTokenAsync(refreshToken, Arg.Any<CancellationToken>()).Returns(true);
        string? capturedPartitionKey = null;
        _service.TryAcquireAsync("refresh", Arg.Do<string>(key => capturedPartitionKey = key), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 1, 10, DateTime.UtcNow.AddMinutes(1)));

        var filter = new DistributedRateLimitFilter("refresh", _service, _authSessionService, _logger);
        var (context, _) = CreateExecutingContext(new AuthController.RefreshSessionRequest(refreshToken));

        await filter.OnActionExecutionAsync(context, () => Task.FromResult(CreateExecutedContext(context)));

        capturedPartitionKey.Should().StartWith("refresh:token:");
        capturedPartitionKey.Should().NotContain(refreshToken);
    }

    [Fact]
    public async Task RefreshPolicy_FallsBackToIpPartition_WhenWellFormedTokenHasNoRealSession()
    {
        var forgedToken = new string('A', RefreshTokenRules.TokenLength);
        _authSessionService.HasSessionForTokenAsync(forgedToken, Arg.Any<CancellationToken>()).Returns(false);
        string? capturedPartitionKey = null;
        _service.TryAcquireAsync("refresh", Arg.Do<string>(key => capturedPartitionKey = key), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 1, 10, DateTime.UtcNow.AddMinutes(1)));

        var filter = new DistributedRateLimitFilter("refresh", _service, _authSessionService, _logger);
        var (context, httpContext) = CreateExecutingContext(new AuthController.RefreshSessionRequest(forgedToken));
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");

        await filter.OnActionExecutionAsync(context, () => Task.FromResult(CreateExecutedContext(context)));

        capturedPartitionKey.Should().StartWith("ip:");
        capturedPartitionKey.Should().NotStartWith("refresh:token:");
    }

    [Fact]
    public async Task ConcurrentChatLimit_SecondRequest_ReturnsMatchingRateLimitResponse()
    {
        var userId = Guid.NewGuid();
        _service.TryAcquireLeaseAsync(
                ConcurrentChatLimitFilter.PolicyName,
                $"user:{userId}",
                1,
                TimeSpan.FromMinutes(5),
                Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 1, 1, DateTime.UtcNow.AddMinutes(4)));
        var filter = new ConcurrentChatLimitFilter(
            _service,
            Substitute.For<ILogger<ConcurrentChatLimitFilter>>());
        var (context, httpContext) = CreateAuthenticatedExecutingContext(userId);
        httpContext.TraceIdentifier = "req_concurrent_chat";

        await filter.OnActionExecutionAsync(
            context,
            () => Task.FromResult(CreateExecutedContext(context)));

        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        httpContext.Response.Headers.RetryAfter.Should().NotBeEmpty();
        httpContext.Response.Headers["X-Orbit-Request-Id"].ToString().Should().Be("req_concurrent_chat");
        await _service.DidNotReceiveWithAnyArgs().ReleaseLeaseAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ConcurrentChatLimit_Success_ReleasesLease()
    {
        var userId = Guid.NewGuid();
        SetupAllowedLease(userId);
        var filter = new ConcurrentChatLimitFilter(
            _service,
            Substitute.For<ILogger<ConcurrentChatLimitFilter>>());
        var (context, _) = CreateAuthenticatedExecutingContext(userId);

        await filter.OnActionExecutionAsync(
            context,
            () => Task.FromResult(CreateExecutedContext(context)));

        await _service.Received(1).ReleaseLeaseAsync(
            ConcurrentChatLimitFilter.PolicyName,
            $"user:{userId}",
            Arg.Any<DateTime>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentChatLimit_ActionException_ReleasesLease()
    {
        var userId = Guid.NewGuid();
        SetupAllowedLease(userId);
        var filter = new ConcurrentChatLimitFilter(
            _service,
            Substitute.For<ILogger<ConcurrentChatLimitFilter>>());
        var (context, _) = CreateAuthenticatedExecutingContext(userId);

        var act = () => filter.OnActionExecutionAsync(
            context,
            () => Task.FromException<ActionExecutedContext>(new InvalidOperationException("action failed")));

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _service.Received(1).ReleaseLeaseAsync(
            ConcurrentChatLimitFilter.PolicyName,
            $"user:{userId}",
            Arg.Any<DateTime>(),
            CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentChatLimit_ClientCancellation_ReleasesLease()
    {
        var userId = Guid.NewGuid();
        SetupAllowedLease(userId);
        var filter = new ConcurrentChatLimitFilter(
            _service,
            Substitute.For<ILogger<ConcurrentChatLimitFilter>>());
        var (context, _) = CreateAuthenticatedExecutingContext(userId);

        var act = () => filter.OnActionExecutionAsync(
            context,
            () => Task.FromException<ActionExecutedContext>(new OperationCanceledException("client disconnected")));

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _service.Received(1).ReleaseLeaseAsync(
            ConcurrentChatLimitFilter.PolicyName,
            $"user:{userId}",
            Arg.Any<DateTime>(),
            CancellationToken.None);
    }

    private void SetupAllowedLease(Guid userId)
    {
        _service.TryAcquireLeaseAsync(
                ConcurrentChatLimitFilter.PolicyName,
                $"user:{userId}",
                1,
                TimeSpan.FromMinutes(5),
                Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 1, 1, DateTime.UtcNow.AddMinutes(5)));
    }

    private static (ActionExecutingContext Context, HttpContext HttpContext) CreateAuthenticatedExecutingContext(Guid userId)
    {
        var result = CreateExecutingContext();
        result.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            "test"));
        return result;
    }

    private static (ActionExecutingContext Context, HttpContext HttpContext) CreateExecutingContext(
        params object?[] actionArguments)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/support";

        var actionContext = new ActionContext(
            httpContext,
            new RouteData(),
            new ControllerActionDescriptor());

        var arguments = new Dictionary<string, object?>();
        for (var index = 0; index < actionArguments.Length; index++)
            arguments[$"arg{index}"] = actionArguments[index];

        var executingContext = new ActionExecutingContext(
            actionContext,
            [],
            arguments,
            controller: new object());

        return (executingContext, httpContext);
    }

    private static ActionExecutedContext CreateExecutedContext(ActionExecutingContext executingContext)
    {
        return new ActionExecutedContext(executingContext, executingContext.Filters, executingContext.Controller);
    }
}
