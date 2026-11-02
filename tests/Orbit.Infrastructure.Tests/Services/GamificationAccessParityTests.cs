using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Gamification.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class GamificationAccessParityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RestMcpAndAstra_UseTheSameAccessRule(bool pro, bool enabled)
    {
        await using var db = new OrbitDbContext(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var user = User.Create("User", "user@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-30));
        if (pro) user.GrantLifetimePro();
        db.Users.Add(user);
        db.AppFeatureFlags.Add(AppFeatureFlag.Create("gamification_free_tier", enabled));
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var flags = new FeatureFlagService(db, cache, Substitute.For<IHostEnvironment>(), new ConfigurationBuilder().Build());
        var progress = Substitute.For<IAchievementProgressService>();
        progress.LoadAsync(Arg.Any<User>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>()).Returns(AchievementProgressMetrics.Empty);
        var handler = new GetGamificationProfileQueryHandler(new GenericRepository<User>(db),
            new GenericRepository<UserAchievement>(db), flags, progress, Substitute.For<IGamificationService>());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<GetGamificationProfileQuery>(), call.Arg<CancellationToken>()));
        var rest = await handler.Handle(new(user.Id, false), default);
        var mcp = await new GamificationTools(mediator).GetGamificationProfile(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test")));
        var evaluator = new AgentPolicyEvaluator(db, new AgentCatalogService(), Substitute.For<IPendingAgentOperationStore>(),
            Substitute.For<IAgentStepUpAuthorizationBridge>(), flags);
        var astra = await evaluator.EvaluateAsync(new(AgentCapabilityIds.GamificationRead, user.Id,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, [], "get_gamification_overview", "Gamification"));
        rest.IsSuccess.Should().Be(pro || enabled);
        mcp.StartsWith("Error:", StringComparison.Ordinal).Should().Be(!(pro || enabled));
        astra.Status.Should().Be(pro || enabled ? AgentPolicyDecisionStatus.Allowed : AgentPolicyDecisionStatus.Denied);
    }
}
