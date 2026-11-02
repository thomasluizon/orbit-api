using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class SlipAlertSnapshotTests
{
    [Fact]
    public async Task UnchangedTick_ReusesProjectedSnapshotAndStillSendsAtLocalTime()
    {
        var counter = new CountingDbCommandInterceptor();
        using var factory = new SqliteOrbitDbContextFactory(counter);
        var db = factory.Context;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var user = User.Create("Slip", "slip-snapshot@example.test").Value;
        user.SetTimeZone("UTC");
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, user.Id, "Slip", FrequencyUnit.Day, 1,
            today.AddDays(-28), IsBadHabit: true, SlipAlertEnabled: true)).Value;
        db.Users.Add(user);
        db.Habits.Add(habit);
        for (var weeks = 1; weeks <= 3; weeks++)
            db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, today.AddDays(-7 * weeks), 1, 0,
                today.AddDays(-7 * weeks).ToDateTime(new TimeOnly(23, 0), DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        var push = Substitute.For<IPushNotificationService>();
        var messages = Substitute.For<ISlipAlertMessageService>();
        messages.GenerateMessageAsync(Arg.Any<string>(), Arg.Any<DayOfWeek>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(("Alert", "Body")));
        using var services = new ServiceCollection().AddScoped(_ => factory.CreateContext())
            .AddSingleton(push).AddSingleton(messages).BuildServiceProvider();
        var clock = new MutableClock(today.ToDateTime(new TimeOnly(20, 59), DateTimeKind.Utc));
        var service = new SlipAlertSchedulerService(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SlipAlertSchedulerService>.Instance, new ConfigurationBuilder().Build(), clock);
        await service.CheckAndSendAlerts(default);
        await push.DidNotReceive().SendManyAsync(Arg.Any<IReadOnlyList<PushDelivery>>(), Arg.Any<CancellationToken>());
        counter.Reset();
        clock.Now = clock.Now.AddMinutes(2);
        await service.CheckAndSendAlerts(default);
        counter.SelectCommandCount.Should().Be(1);
        await push.Received(1).SendManyAsync(Arg.Is<IReadOnlyList<PushDelivery>>(items =>
            items.Count == 1 && items[0].UserId == user.Id && items[0].Title == "Alert" && items[0].Body == "Body"),
            Arg.Any<CancellationToken>());
        db.HabitLogs.Add(HabitLog.FromScheduleRead(Guid.NewGuid(), habit.Id, today.AddDays(-1), 1, 0,
            today.AddDays(-1).ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc)));
        await db.SaveChangesAsync();
        counter.Reset();
        await service.CheckAndSendAlerts(default);
        counter.SelectCommandCount.Should().Be(5);
        counter.Commands.Where(c => c.Sql.Contains("\"HabitLogs\"", StringComparison.Ordinal))
            .Should().OnlyContain(c => !c.Sql.Contains("\"Note\"", StringComparison.Ordinal));
    }

    private sealed class MutableClock(DateTime initial) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = initial;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
