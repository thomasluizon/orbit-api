using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ScheduledJobPagingTests
{
    [Theory]
    [InlineData("goals")]
    [InlineData("streak-goals")]
    [InlineData("due-dates")]
    [InlineData("period-close")]
    [InlineData("streak-freezes")]
    [InlineData("proactive")]
    public async Task Run_TwelveHundredCandidates_EachPopulationReadIsBounded(string kind)
    {
        var commands = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(commands);
        var db = factory.Context;
        var today = DateOnly.FromDateTime(TimeProvider.System.GetUtcNow().UtcDateTime);
        for (var index = 0; index < 1200; index++)
        {
            var user = User.Create($"User {index}", $"user{index}@example.com").Value;
            user.GrantLifetimePro();
            if (kind == "streak-freezes")
            {
                typeof(User).GetProperty(nameof(User.CurrentStreak))!.SetValue(user, 1);
                typeof(User).GetProperty(nameof(User.StreakFreezesAccumulated))!.SetValue(user, 1);
            }
            db.Add(user);
            if (kind is "goals" or "streak-goals")
                db.Add(Goal.Create(new Goal.CreateGoalParams(user.Id, "Goal", 100, "units",
                    Deadline: today.AddDays(20), Type: GoalType.Streak)).Value);
            if (kind == "due-dates")
                db.Add(Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Habit", FrequencyUnit.Day, 1,
                    DueDate: today.AddDays(-5), IsBadHabit: true)).Value);
            if (kind == "period-close")
                db.Add(PushSubscription.Create(user.Id, $"device-{index}", "fcm", "auth").Value);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        commands.Reset();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(today);
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(today);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUsersAsync(Arg.Any<IReadOnlyCollection<User>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<User>>().ToDictionary(user => user.Id, _ => (IReadOnlyList<string>)Array.Empty<string>()));
        var push = Substitute.For<IPushNotificationService>();
        var config = new ConfigurationBuilder().Build();
        using var provider = new ServiceCollection().AddScoped(_ => factory.CreateContext())
            .AddSingleton(dates).AddSingleton(flags).AddSingleton(push)
            .AddSingleton(Substitute.For<IGoalCompletionService>()).BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        IScheduledJob? job = kind switch
        {
            "goals" => new GoalDeadlineNotificationService(scopes, NullLogger<GoalDeadlineNotificationService>.Instance, config),
            "streak-goals" => new StreakGoalSyncService(scopes, NullLogger<StreakGoalSyncService>.Instance, config),
            "due-dates" => new HabitDueDateAdvancementService(scopes, NullLogger<HabitDueDateAdvancementService>.Instance, config, TimeProvider.System),
            "period-close" => new PeriodCloseNotificationService(scopes, NullLogger<PeriodCloseNotificationService>.Instance, config),
            "streak-freezes" => new StreakFreezeAutoActivationService(scopes, NullLogger<StreakFreezeAutoActivationService>.Instance, config),
            _ => null
        };
        if (job is not null) await job.RunAsync(default);
        else
        {
            var engine = new ProactiveEngineService(db, TimeProvider.System, Substitute.For<IProactiveCheckinMessageService>(), ProactiveEngineServiceTests.ExecutingJobs(push),
                Substitute.For<ICalendarEventFetcher>(), Substitute.For<IGoogleTokenService>(), Substitute.For<IProductAnalytics>(), dates,
                flags, NullLogger<ProactiveEngineService>.Instance);
            await engine.EvaluateAsync(default);
        }
        commands.Commands.Should().NotBeEmpty().And.OnlyContain(command => command.Rows <= 400);
    }
}
