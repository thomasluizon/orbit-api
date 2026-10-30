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
using Orbit.Domain.Common;
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
    [InlineData(0, "America/Sao_Paulo", "2026-08-02T03:00:00Z", 50, 1, 6, 8)]
    [InlineData(1, "Pacific/Auckland", "2026-08-02T12:00:00Z", 50, 0, 6, 13)]
    [InlineData(0, "America/Sao_Paulo", "2026-08-02T03:00:00Z", 250, 1, 7, 8)]
    [InlineData(1, "Pacific/Auckland", "2026-08-02T12:00:00Z", 250, 0, 7, 13)]
    public async Task Pro_Allows250AcrossLocalWeekThenPayGateUntilNextWeek(int weekStartDay, string zone, string start,
        int messagesPerDay, int? newWeekStartDay = null, int changeDay = 6, int periodDays = 7)
    {
        var fixture = new AllowanceFixture(weekStartDay, zone, DateTimeOffset.Parse(start));
        var user = fixture.User;
        var users = fixture.Users;
        var clock = fixture.Clock;
        var dates = fixture.Dates;
        var gate = fixture.Gate;
        var work = fixture.Work;
        var weekStart = clock.Now;

        for (var index = 0; index < 250; index++)
        {
            clock.Now = weekStart.AddDays(index / messagesPerDay);
            var accepted = await gate.TryConsumeAiMessage(user.Id, work);
            accepted.IsSuccess.Should().BeTrue($"message {index + 1} belongs to the same local week");
        }

        if (newWeekStartDay.HasValue)
        {
            clock.Now = weekStart.AddDays(changeDay);
            user.SetWeekStartDay(newWeekStartDay.Value).IsSuccess.Should().BeTrue();
        }
        for (var day = changeDay; day < periodDays; day++)
        {
            clock.Now = weekStart.AddDays(day);
            (await gate.TryConsumeAiMessage(user.Id, work)).ErrorCode.Should().Be(ErrorCodes.PayGate);
            user.GetAiMessagesUsedForQuota(await dates.GetUserTodayAsync(user.Id)).Should().Be(250);
        }
        clock.Now = weekStart.AddDays(periodDays).AddTicks(-1);
        var refused = await gate.TryConsumeAiMessage(user.Id, work);
        refused.ErrorCode.Should().Be(ErrorCodes.PayGate);
        (await gate.GetAiMessageLimit(user.Id)).Should().Be(250);
        var statusHandler = new GetSubscriptionStatusQueryHandler(users, gate, dates);
        var status = (await statusHandler.Handle(new(user.Id), default)).Value;
        status.AiMessagesUsed.Should().Be(250);
        status.AiMessagesLimit.Should().Be(250);
        status.AiMessagesPeriod.Should().Be("week");
        status.AiMessagesResetsAt.Should().Be(weekStart.AddDays(periodDays).UtcDateTime);
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
        profile.AiMessagesResetsAt.Should().Be(weekStart.AddDays(periodDays).UtcDateTime);
        var planCard = AccountRowsCardBuilder.Plan(profile);
        planCard.AiMessagesPeriod.Should().Be("week");
        planCard.AiMessagesResetsAt.Should().Be(profile.AiMessagesResetsAt);
        clock.Now = weekStart.AddDays(periodDays);
        (await gate.TryConsumeAiMessage(user.Id, work)).IsSuccess.Should().BeTrue();
        user.GetAiMessagesUsedForQuota(await dates.GetUserTodayAsync(user.Id)).Should().Be(1);
        var nextLocalDate = await dates.GetUserTodayAsync(user.Id);
        var nextPeriod = AiAllowancePeriod.For(nextLocalDate, true, user.WeekStartDay);
        nextPeriod.Start.Should().Be(nextLocalDate);
        user.AiMessagesWeekStart.Should().Be(nextPeriod.Start);
        var nextStatus = (await statusHandler.Handle(new(user.Id), default)).Value;
        var zoneInfo = TimeZoneInfo.FindSystemTimeZoneById(zone);
        nextStatus.AiMessagesResetsAt.Should().Be(TimeZoneInfo.ConvertTimeToUtc(
            nextPeriod.End.ToDateTime(TimeOnly.MinValue), zoneInfo));
        await work.Received(251).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pro_DailyWeekStartPreferenceChanges_NeverGrantOverlappingPools()
    {
        var start = DateTimeOffset.Parse("2026-08-02T03:00:00Z");
        var fixture = new AllowanceFixture(0, "America/Sao_Paulo", start);
        DateOnly? previousWeekStart = null;
        var pools = 0;

        for (var day = 0; day < 35; day++)
        {
            fixture.Clock.Now = start.AddDays(day);
            if (day >= 7)
                fixture.User.SetWeekStartDay(day % 2).IsSuccess.Should().BeTrue();
            var today = await fixture.Dates.GetUserTodayAsync(fixture.User.Id);
            var accepted = await fixture.Gate.TryConsumeAiMessage(fixture.User.Id, fixture.Work);
            if (accepted.IsSuccess)
            {
                fixture.User.AiMessagesWeekStart.Should().NotBeNull();
                var weekStart = fixture.User.AiMessagesWeekStart.GetValueOrDefault();
                if (previousWeekStart.HasValue)
                    (weekStart.DayNumber - previousWeekStart.Value.DayNumber).Should().BeGreaterThanOrEqualTo(7);
                previousWeekStart = weekStart;
                pools++;
                for (var message = 1; message < 250; message++)
                    (await fixture.Gate.TryConsumeAiMessage(fixture.User.Id, fixture.Work)).IsSuccess.Should().BeTrue();
            }
            else
            {
                accepted.ErrorCode.Should().Be(ErrorCodes.PayGate);
            }

            var period = AiAllowancePeriod.ActiveFor(today, true, fixture.User.WeekStartDay, fixture.User.AiMessagesWeekStart);
            (period.End.DayNumber - period.Start.DayNumber).Should().BeGreaterThanOrEqualTo(7);
            period.End.Should().BeAfter(today);
            fixture.User.GetAiMessagesUsedForQuota(today).Should().Be(250);
            (await fixture.Gate.TryConsumeAiMessage(fixture.User.Id, fixture.Work)).ErrorCode.Should().Be(ErrorCodes.PayGate);
        }

        pools.Should().Be(5);
    }

    private sealed class AllowanceFixture
    {
        public User User { get; } = User.Create("Pro", "pro@example.com").Value;
        public IGenericRepository<User> Users { get; } = Substitute.For<IGenericRepository<User>>();
        public IUnitOfWork Work { get; } = Substitute.For<IUnitOfWork>();
        public AllowanceClock Clock { get; }
        public UserDateService Dates { get; }
        public PayGateService Gate { get; }
        private readonly MemoryDistributedCache _cache = new(Options.Create(new MemoryDistributedCacheOptions()));

        public AllowanceFixture(int weekStartDay, string zone, DateTimeOffset start)
        {
            User.GrantLifetimePro();
            User.SetTimeZone(zone).IsSuccess.Should().BeTrue();
            User.SetWeekStartDay(weekStartDay).IsSuccess.Should().BeTrue();
            Users.GetByIdAsync(User.Id, Arg.Any<CancellationToken>()).Returns(User);
            Users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(User);
            var config = Substitute.For<IAppConfigService>();
            config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
            config.GetAsync("ProAiMessagesPerWeek", 250, Arg.Any<CancellationToken>()).Returns(250);
            Clock = new AllowanceClock { Now = start };
            Dates = new UserDateService(Users, _cache, Clock, Substitute.For<IUnitOfWork>());
            Gate = new PayGateService(Substitute.For<IGenericRepository<Habit>>(), Users, config, Dates,
                Substitute.For<IFeatureFlagService>());
        }
    }

    private sealed class AllowanceClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
