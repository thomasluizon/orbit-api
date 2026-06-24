using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.BackgroundJobs;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.BackgroundJobs;

/// <summary>
/// Guards the durable-queue registry: every recurring scheduler exposes the <see cref="IScheduledJob"/>
/// contract Hangfire registration depends on, and the job names that key Hangfire's storage are unique
/// so re-registration never silently overwrites (loses) a job.
/// </summary>
public class ScheduledJobRegistryTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    public static IEnumerable<object[]> AllScheduledJobs() =>
        BuildAll().Select(job => new object[] { job });

    [Theory]
    [MemberData(nameof(AllScheduledJobs))]
    public void Job_HasNameAndCronExpression(IScheduledJob job)
    {
        job.Name.Should().NotBeNullOrWhiteSpace();
        job.CronExpression.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void JobNames_AreUnique()
    {
        var names = BuildAll().Select(job => job.Name).ToList();

        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AllElevenRecurringSchedulers_AreRegisteredAsJobs()
    {
        BuildAll().Should().HaveCount(11);
    }

    [Fact]
    public async Task RunAsync_ExecutesUnderlyingScan_WithoutDoubleRunningSideEffects()
    {
        await using var dbContext = NewDbContext();
        var pushService = Substitute.For<IPushNotificationService>();
        var scopeFactory = ScopeFactoryFor(dbContext, pushService);

        var reminder = new ReminderSchedulerService(
            scopeFactory, NullLogger<ReminderSchedulerService>.Instance, EmptyConfiguration);

        await ((IScheduledJob)reminder).RunAsync(CancellationToken.None);

        await pushService.DidNotReceive().SendToUserAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private static List<IScheduledJob> BuildAll() =>
    [
        new ReminderSchedulerService(ScopeFactory(), NullLogger<ReminderSchedulerService>.Instance, EmptyConfiguration),
        new GoalDeadlineNotificationService(ScopeFactory(), NullLogger<GoalDeadlineNotificationService>.Instance, EmptyConfiguration),
        new SlipAlertSchedulerService(ScopeFactory(), NullLogger<SlipAlertSchedulerService>.Instance, EmptyConfiguration),
        new AccountDeletionService(ScopeFactory(), NullLogger<AccountDeletionService>.Instance, EmptyConfiguration),
        new HabitDueDateAdvancementService(ScopeFactory(), NullLogger<HabitDueDateAdvancementService>.Instance, EmptyConfiguration),
        new StreakGoalSyncService(ScopeFactory(), NullLogger<StreakGoalSyncService>.Instance, EmptyConfiguration),
        new StreakFreezeAutoActivationService(ScopeFactory(), NullLogger<StreakFreezeAutoActivationService>.Instance, EmptyConfiguration),
        new SyncCleanupService(ScopeFactory(), NullLogger<SyncCleanupService>.Instance),
        new PlayNotificationCleanupService(ScopeFactory(), NullLogger<PlayNotificationCleanupService>.Instance),
        new CalendarAutoSyncService(ScopeFactory(), NullLogger<CalendarAutoSyncService>.Instance, EmptyConfiguration, TimeProvider.System),
        new OpenAiBatchPollerService(ScopeFactory(), NullLogger<OpenAiBatchPollerService>.Instance, EmptyConfiguration),
    ];

    private static IServiceScopeFactory ScopeFactory() => Substitute.For<IServiceScopeFactory>();

    private static OrbitDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase($"ScheduledJobRegistryTests_{Guid.NewGuid()}")
            .Options);

    private static IServiceScopeFactory ScopeFactoryFor(OrbitDbContext dbContext, IPushNotificationService pushService)
    {
        var provider = new ServiceCollection()
            .AddSingleton(dbContext)
            .AddSingleton(pushService)
            .BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }
}
