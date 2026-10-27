using System.Linq.Expressions;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Chat;
using Orbit.Application.Profile.Queries;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Application.Tests.Common;

public class WeeklyAiAllowanceTests
{
    [Theory]
    [InlineData(0, "America/Sao_Paulo", "2026-08-02T03:00:00Z", 50)]
    [InlineData(1, "Pacific/Auckland", "2026-08-02T12:00:00Z", 50)]
    [InlineData(0, "America/Sao_Paulo", "2026-08-02T03:00:00Z", 250)]
    [InlineData(1, "Pacific/Auckland", "2026-08-02T12:00:00Z", 250)]
    public async Task Pro_Allows250AcrossLocalWeekThenPayGateUntilNextWeek(int weekStartDay, string zone, string start, int messagesPerDay)
    {
        var user = User.Create("Pro", "pro@example.com").Value;
        user.GrantLifetimePro();
        user.SetTimeZone(zone).IsSuccess.Should().BeTrue();
        user.SetWeekStartDay(weekStartDay).IsSuccess.Should().BeTrue();
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(user);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
        config.GetAsync("ProAiMessagesPerWeek", 250, Arg.Any<CancellationToken>()).Returns(250);
        var clock = new AllowanceClock { Now = DateTimeOffset.Parse(start) };
        var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var dates = new UserDateService(users, cache, clock);
        var gate = new PayGateService(Substitute.For<IGenericRepository<Habit>>(), users, config, dates,
            Substitute.For<IFeatureFlagService>());
        var work = Substitute.For<IUnitOfWork>();
        var weekStart = clock.Now;

        for (var index = 0; index < 250; index++)
        {
            clock.Now = weekStart.AddDays(index / messagesPerDay);
            var accepted = await gate.TryConsumeAiMessage(user.Id, work);
            accepted.IsSuccess.Should().BeTrue($"message {index + 1} belongs to the same local week");
        }

        clock.Now = weekStart.AddDays(7).AddTicks(-1);
        var refused = await gate.TryConsumeAiMessage(user.Id, work);
        refused.ErrorCode.Should().Be(ErrorCodes.PayGate);
        (await gate.GetAiMessageLimit(user.Id)).Should().Be(250);
        var statusHandler = new GetSubscriptionStatusQueryHandler(users, gate, dates);
        var status = (await statusHandler.Handle(new(user.Id), default)).Value;
        status.AiMessagesUsed.Should().Be(250);
        status.AiMessagesLimit.Should().Be(250);
        status.AiMessagesPeriod.Should().Be("week");
        status.AiMessagesResetsAt.Should().Be(weekStart.AddDays(7).UtcDateTime);
        var freezes = Substitute.For<IGenericRepository<StreakFreeze>>();
        freezes.FindAsync(Arg.Any<Expression<Func<StreakFreeze, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<StreakFreeze>());
        var profileHandler = new GetProfileQueryHandler(users, Substitute.For<IGenericRepository<ApiKey>>(), freezes,
            Substitute.For<IHabitLogReader>(), new(dates, Substitute.For<IUserStreakService>()),
            Substitute.For<IFeatureFlagService>(), gate);
        var profile = (await profileHandler.Handle(new(user.Id), default)).Value;
        profile.AiMessagesUsed.Should().Be(250);
        profile.AiMessagesLimit.Should().Be(250);
        profile.AiMessagesPeriod.Should().Be("week");
        profile.AiMessagesResetsAt.Should().Be(weekStart.AddDays(7).UtcDateTime);
        var planCard = AccountRowsCardBuilder.Plan(profile);
        planCard.AiMessagesPeriod.Should().Be("week");
        planCard.AiMessagesResetsAt.Should().Be(profile.AiMessagesResetsAt);
        clock.Now = weekStart.AddDays(7);
        (await gate.TryConsumeAiMessage(user.Id, work)).IsSuccess.Should().BeTrue();
        user.GetAiMessagesUsedForQuota(await dates.GetUserTodayAsync(user.Id)).Should().Be(1);
        await work.Received(251).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private sealed class AllowanceClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
