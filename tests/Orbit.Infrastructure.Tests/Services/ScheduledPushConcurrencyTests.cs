using System.Collections.Concurrent;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ScheduledPushConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParallelRecipients_RealPushService_UsesIndependentContextsAndDeliversEveryPush(bool freeze)
    {
        var queries = new DelayedSubscriptionQuery();
        using var factory = new SqliteOrbitDbContextFactory(Path.Combine(Path.GetTempPath(), $"orbit-push-{Guid.NewGuid():N}.db"), queries);
        var db = factory.Context;
        var today = new DateOnly(2026, 6, 1);
        var recipients = Enumerable.Range(0, 4).Select(index =>
        {
            var user = User.Create($"Push {index}", $"push{index}@example.com").Value;
            user.GrantLifetimePro();
            user.SetStreakState(10, 10, today.AddDays(-2));
            user.AwardStreakFreezeIfEligible();
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Daily",
                FrequencyUnit.Day, 1, DueDate: today.AddDays(-6))).Value;
            typeof(Habit).GetProperty(nameof(Habit.CreatedAtUtc))!.SetValue(habit, today.AddDays(-6).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            foreach (var offset in new[] { -5, -4, -3, -2 }) habit.Log(today.AddDays(offset), advanceDueDate: false);
            db.AddRange(user, habit, PushSubscription.Create(user.Id, $"device-{index}", PushSubscription.ApnsSentinel, "secret").Value);
            return user.Id;
        }).ToArray();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(today);
        var features = Substitute.For<IFeatureFlagService>();
        features.GetEnabledKeysForUsersAsync(Arg.Any<IReadOnlyCollection<User>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<string>>());
        var apns = Substitute.For<IApnsPushSender>();
        apns.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>()).Returns(ApnsDeliveryOutcome.Delivered);
        using var http = new HttpClient();
        using var services = new ServiceCollection()
            .AddScoped(_ => factory.CreateContext())
            .AddScoped<IPushNotificationService>(scope => new PushNotificationService(scope.GetRequiredService<OrbitDbContext>(),
                Options.Create(new VapidSettings()), NullLogger<PushNotificationService>.Instance, http, apns, Substitute.For<IWnsPushSender>()))
            .AddSingleton(dates).AddSingleton(features).BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["BackgroundServices:PushParallelism"] = "2" }).Build();
        queries.Enabled = true;

        if (freeze)
            await new StreakFreezeAutoActivationService(services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<StreakFreezeAutoActivationService>.Instance, configuration).RunAsync(default);
        else
            await new PeriodCloseNotificationService(services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<PeriodCloseNotificationService>.Instance, configuration).RunAsync(default);

        await apns.Received(4).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<IReadOnlyDictionary<string, string>?>());
        queries.Contexts.Distinct().Should().HaveCount(4);
        queries.MaxConcurrent.Should().Be(2);
        (await db.Notifications.AsNoTracking().Select(row => row.UserId).ToListAsync()).Should().BeEquivalentTo(recipients);
        if (freeze) (await db.StreakFreezes.CountAsync()).Should().Be(4);
    }

    private sealed class DelayedSubscriptionQuery : DbCommandInterceptor
    {
        internal bool Enabled { get; set; }
        internal ConcurrentBag<Guid> Contexts { get; } = [];
        private int _active;
        internal int MaxConcurrent { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Enabled || !command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                || !command.CommandText.Contains("FROM \"PushSubscriptions\" AS", StringComparison.Ordinal)
                || command.CommandText.Contains("FROM \"Users\"", StringComparison.Ordinal)) return result;
            Contexts.Add(eventData.Context!.ContextId.InstanceId);
            var active = Interlocked.Increment(ref _active);
            lock (Contexts) MaxConcurrent = Math.Max(MaxConcurrent, active);
            try { await Task.Delay(150, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
            return result;
        }
    }
}
