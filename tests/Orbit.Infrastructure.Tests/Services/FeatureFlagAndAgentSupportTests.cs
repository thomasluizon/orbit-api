using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.RateLimiting;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class FeatureFlagAndAgentSupportTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;

    public FeatureFlagAndAgentSupportTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"FeatureFlagAndAgentSupportTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FeatureFlagService_ReturnsEnabledFlagsForMatchingPlan()
    {
        var user = User.Create("Thomas", "thomas@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Yearly);
        _dbContext.Users.Add(user);
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("basic", true, null, "Basic"));
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("pro_only", true, "pro", "Pro"));
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("disabled", false, null, "Disabled"));
        await _dbContext.SaveChangesAsync();

        var service = new FeatureFlagService(_dbContext);

        var result = await service.GetEnabledKeysForUserAsync(user.Id);

        result.Should().Equal("basic", "pro_only");
    }

    [Fact]
    public async Task FeatureFlagService_ReturnsEmptyWhenUserIsMissing()
    {
        var service = new FeatureFlagService(_dbContext);

        var result = await service.GetEnabledKeysForUserAsync(Guid.NewGuid());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task AgentTargetOwnershipService_ReturnsNullWhenNoTargetsAreProvided()
    {
        var service = new AgentTargetOwnershipService(_dbContext);

        var result = await service.GetDenialReasonAsync("delete_habit", Guid.NewGuid(), Parse("{}"));

        result.Should().BeNull();
    }

    [Fact]
    public async Task AgentTargetOwnershipService_ReturnsDenialWhenHabitIsNotOwned()
    {
        var owner = User.Create("Owner", "owner@example.com").Value;
        var otherUser = User.Create("Other", "other@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(
            owner.Id,
            "Exercise",
            FrequencyUnit.Day,
            1,
            DueDate: DateOnly.FromDateTime(DateTime.UtcNow))).Value;

        _dbContext.Users.AddRange(owner, otherUser);
        _dbContext.Habits.Add(habit);
        await _dbContext.SaveChangesAsync();

        var service = new AgentTargetOwnershipService(_dbContext);

        var result = await service.GetDenialReasonAsync(
            "delete_habit",
            otherUser.Id,
            Parse($$"""{"habit_id":"{{habit.Id}}"}"""));

        result.Should().Be("target_not_owned:delete_habit:habit");
    }

    [Fact]
    public async Task AgentAuditService_PersistsAuditLog()
    {
        var service = new AgentAuditService(_dbContext);
        var entry = new AgentAuditEntry(
            Guid.NewGuid(),
            AgentCapabilityIds.HabitsRead,
            "list_habits",
            AgentExecutionSurface.Mcp,
            AgentAuthMethod.Jwt,
            AgentRiskClass.Low,
            AgentPolicyDecisionStatus.Allowed,
            AgentOperationStatus.Succeeded,
            Summary: "Read habits");

        await service.RecordAsync(entry);

        _dbContext.AgentAuditLogs.Should().ContainSingle(log =>
            log.CapabilityId == AgentCapabilityIds.HabitsRead &&
            log.SourceName == "list_habits");
    }

    [Fact]
    public void DistributedRateLimitAttribute_CreatesFilterWithResolvedService()
    {
        var rateLimitService = Substitute.For<IDistributedRateLimitService>();
        var logger = Substitute.For<ILogger<DistributedRateLimitFilter>>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IDistributedRateLimitService)).Returns(rateLimitService);
        serviceProvider.GetService(typeof(ILogger<DistributedRateLimitFilter>)).Returns(logger);
        var attribute = new DistributedRateLimitAttribute("chat");

        var filter = attribute.CreateInstance(serviceProvider);

        filter.Should().BeOfType<DistributedRateLimitFilter>();
    }

    [Fact]
    public async Task DistributedRateLimitFilter_UsesAuthenticatedUserPartitionAndCallsNext()
    {
        var userId = Guid.NewGuid();
        var rateLimitService = Substitute.For<IDistributedRateLimitService>();
        var logger = Substitute.For<ILogger<DistributedRateLimitFilter>>();
        rateLimitService.TryAcquireAsync("chat", $"user:{userId}", Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 20, 1, DateTime.UtcNow.AddSeconds(30)));
        var filter = new DistributedRateLimitFilter("chat", rateLimitService, logger);
        var context = CreateActionExecutingContext(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString())
            ], "Test"))
        });
        var nextCalled = false;

        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        });

        nextCalled.Should().BeTrue();
        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task DistributedRateLimitFilter_ReturnsTooManyRequestsForAnonymousIp()
    {
        var rateLimitService = Substitute.For<IDistributedRateLimitService>();
        var logger = Substitute.For<ILogger<DistributedRateLimitFilter>>();
        var retryAt = DateTime.UtcNow.AddSeconds(15);
        rateLimitService.TryAcquireAsync("auth", "ip:203.0.113.10", Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(false, 5, 5, retryAt));
        var filter = new DistributedRateLimitFilter("auth", rateLimitService, logger);
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        httpContext.TraceIdentifier = "req_rate_limited";
        var context = CreateActionExecutingContext(httpContext);

        await filter.OnActionExecutionAsync(context, () =>
            Task.FromResult(new ActionExecutedContext(context, [], new object())));

        var result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        httpContext.Response.Headers.RetryAfter.Should().NotBeEmpty();
        httpContext.Response.Headers[HttpContextExtensions.RequestIdHeaderName].ToString().Should().Be("req_rate_limited");
    }

    private static ActionExecutingContext CreateActionExecutingContext(HttpContext httpContext)
    {
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
