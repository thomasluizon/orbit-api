using HttpContextExtensions = Orbit.Api.Extensions.HttpContextExtensions;
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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly DbContextOptions<OrbitDbContext> _options;

    public FeatureFlagAndAgentSupportTests()
    {
        _options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"FeatureFlagAndAgentSupportTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(_options);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("tag_id", false)]
    [InlineData("tag_ids", true)]
    public async Task Ownership_RefusesForeignTags(string argument, bool array)
    {
        var owner = Guid.NewGuid();
        var tag = Tag.Create(owner, "Private", "#ffffff").Value;
        _dbContext.Tags.Add(tag);
        await _dbContext.SaveChangesAsync();
        var args = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            [argument] = array ? new[] { tag.Id } : (object)tag.Id
        });
        var service = new AgentTargetOwnershipService(_dbContext);
        (await service.GetDenialReasonAsync("assign_tags", Guid.NewGuid(), args))
            .Should().Be("target_not_owned:assign_tags:tag");
        (await service.GetDenialReasonAsync("assign_tags", owner, args)).Should().BeNull();
    }

    [Fact]
    public async Task Ownership_RefusesForeignCalendarSuggestion()
    {
        var suggestion = GoogleCalendarSyncSuggestion.Create(Guid.NewGuid(), "event", "Private",
            DateTime.UtcNow, "{}", DateTime.UtcNow);
        _dbContext.GoogleCalendarSyncSuggestions.Add(suggestion);
        await _dbContext.SaveChangesAsync();
        var args = JsonSerializer.SerializeToElement(new { suggestion_id = suggestion.Id });
        var service = new AgentTargetOwnershipService(_dbContext);
        (await service.GetDenialReasonAsync("import_calendar_event", Guid.NewGuid(), args))
            .Should().Be("target_not_owned:import_calendar_event:calendar_suggestion");
        (await service.GetDenialReasonAsync("import_calendar_event", suggestion.UserId, args)).Should().BeNull();
    }

    [Fact]
    public async Task FeatureFlagService_ResolvesTargetsAndPlansAndInvalidatesWrites()
    {
        var accountA = User.Create("A", "a@example.com").Value;
        var accountB = User.Create("B", "b@example.com").Value;
        accountA.StartTrial(DateTime.UtcNow.AddDays(-1));
        _dbContext.Users.AddRange(accountA, accountB);
        _dbContext.AppFeatureFlags.AddRange(AppFeatureFlag.Create("targeted", false), AppFeatureFlag.Create("paid", false, "Pro"));
        _dbContext.AppFeatureFlagAccounts.AddRange(AppFeatureFlagAccount.Create("targeted", accountA.Id), AppFeatureFlagAccount.Create("paid", accountA.Id));
        await _dbContext.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FeatureFlagService(_dbContext, cache);

        (await service.GetEnabledKeysForUserAsync(accountA.Id)).Should().Equal("targeted");
        (await service.GetEnabledKeysForUserAsync(accountB.Id)).Should().BeEmpty();
        service.ResolveForUser(accountA.Id).Should().Equal(await service.ResolveForUserAsync(accountA.Id));
        accountA.SetStripeSubscription("sub_paid", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);
        await _dbContext.SaveChangesAsync();
        (await service.GetEnabledKeysForUserAsync(accountA.Id)).Should().Equal("paid", "targeted");

        _dbContext.AppFeatureFlagAccounts.RemoveRange(_dbContext.AppFeatureFlagAccounts);
        await _dbContext.SaveChangesAsync();
        service.Invalidate();
        (await service.GetEnabledKeysForUserAsync(accountA.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task FeatureFlagService_ReturnsEnabledFlagsForMatchingPlan()
    {
        var user = User.Create("Alex", "alex@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Yearly);
        _dbContext.Users.Add(user);
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("basic", true, null, "Basic"));
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("pro_only", true, "pro", "Pro"));
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("disabled", false, null, "Disabled"));
        await _dbContext.SaveChangesAsync();

        var service = new FeatureFlagService(_dbContext, new MemoryCache(new MemoryCacheOptions()));

        var result = await service.GetEnabledKeysForUserAsync(user.Id);

        result.Should().Equal("basic", "pro_only");
    }

    [Fact]
    public async Task FeatureFlagService_ReturnsEmptyWhenUserIsMissing()
    {
        var service = new FeatureFlagService(_dbContext, new MemoryCache(new MemoryCacheOptions()));

        var result = await service.GetEnabledKeysForUserAsync(Guid.NewGuid());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FeatureFlagService_UnknownPlanRequirement_FailsClosed()
    {
        var user = User.Create("Alex", "alex@example.com").Value;
        user.SetStripeSubscription("sub_123", DateTime.UtcNow.AddDays(30), SubscriptionInterval.Monthly);
        var unknownRequirement = string.Concat("Yearly", "Pro");
        _dbContext.Users.Add(user);
        _dbContext.AppFeatureFlags.Add(AppFeatureFlag.Create("stale_plan", true, unknownRequirement, "Stale plan"));
        await _dbContext.SaveChangesAsync();

        var service = new FeatureFlagService(_dbContext, new MemoryCache(new MemoryCacheOptions()));

        var result = await service.GetEnabledKeysForUserAsync(user.Id);

        result.Should().NotContain("stale_plan");
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
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
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
        using var services = new ServiceCollection()
            .AddScoped(_ => new OrbitDbContext(_options))
            .AddScoped<IAgentAuditService, AgentAuditService>()
            .BuildServiceProvider();
        using var scope = services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IAgentAuditService>();
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
        serviceProvider.GetService(typeof(IAuthSessionService)).Returns(Substitute.For<IAuthSessionService>());
        serviceProvider.GetService(typeof(IOptions<BffForwardingSettings>)).Returns(Options.Create(new BffForwardingSettings()));
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
        var filter = new DistributedRateLimitFilter("chat", rateLimitService, Substitute.For<IAuthSessionService>(), Options.Create(new BffForwardingSettings()), logger);
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
        var filter = new DistributedRateLimitFilter("auth", rateLimitService, Substitute.For<IAuthSessionService>(), Options.Create(new BffForwardingSettings()), logger);
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
