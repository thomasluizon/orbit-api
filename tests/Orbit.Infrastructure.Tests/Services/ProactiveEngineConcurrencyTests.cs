using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public class ProactiveEngineConcurrencyTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task Evaluate_CalendarTokenWrite_PreservesClientChangeAndCreatesItems(int concurrentWrites, bool persistentConflict)
    {
        await using var database = new UserRowConcurrencyHarness(user => user.SetName("Client name"), maxRaces: 0);
        var user = User.Create("Proactive user", "proactive@example.com").Value;
        user.GrantLifetimePro();
        user.SetTimeZone("UTC");
        user.SetGoogleTokens("access", "refresh");
        await database.SeedAsync(user);
        var today = new DateOnly(2030, 10, 15);
        database.Context.Habits.Add(Habit.Create(new HabitCreateParams(user.Id, "Read", FrequencyUnit.Day, 1, today)).Value);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        database.ForceUserConflict = persistentConflict;
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<string?>(), user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUsersAsync(Arg.Any<IReadOnlyList<User>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>>(async _ =>
            {
                if (concurrentWrites > 0) await database.WriteAsync();
                return new Dictionary<Guid, IReadOnlyList<string>> { [user.Id] = [FeatureFlagKeys.CalendarIntegration] };
            });
        var messages = Substitute.For<IProactiveCheckinMessageService>();
        messages.GenerateAsync(Arg.Any<ProactiveFacts>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProactiveMessage("Plan your day.", "Plan", "Help me plan."));
        var tokens = Substitute.For<IGoogleTokenService>();
        tokens.TryRefreshAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<User>().SetGoogleTokens("refreshed", null);
            return new GoogleTokenRefreshOutcome("refreshed", GoogleTokenRefreshResult.Success, null);
        });
        var calendar = Substitute.For<ICalendarEventFetcher>();
        calendar.FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), null, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, 4).Select(index => new CalendarEventItem(
                $"event-{index}", "Meeting", null, "2030-10-15", "09:00", "10:00", false, null, [],
                today.ToDateTime(new TimeOnly(9 + index, 0), DateTimeKind.Utc),
                EndUtc: today.ToDateTime(new TimeOnly(10 + index, 0), DateTimeKind.Utc))).ToList());
        var analytics = Substitute.For<IProductAnalytics>();
        var engine = new ProactiveEngineService(database.Context,
            new ProactiveEngineServiceTests.FixedClock(today.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc)),
            messages, Substitute.For<IPushNotificationService>(), calendar, tokens, analytics,
            dates, flags, NullLogger<ProactiveEngineService>.Instance);

        await engine.EvaluateAsync(default);

        await using var verify = database.CreateContext();
        (await verify.ProactiveItems.CountAsync(item => item.Trigger == ProactiveTrigger.MorningPlan)).Should().Be(1);
        analytics.Received().CaptureUserEvent(user.Id, "astra_proactive_item_created", Arg.Any<string>(),
            Arg.Is<Dictionary<string, object>>(properties => (string)properties["trigger"] == nameof(ProactiveTrigger.MorningPlan)));
        database.Context.ChangeTracker.Entries<User>().Should().OnlyContain(entry => entry.State == EntityState.Unchanged);
        var saved = await verify.Users.SingleAsync();
        saved.Name.Should().Be(concurrentWrites == 0 ? "Proactive user" : "Client name");
        if (!persistentConflict)
        {
            saved.GoogleAccessToken.Should().Be("refreshed");
            (await verify.ProactiveItems.CountAsync(item => item.Trigger == ProactiveTrigger.BusyCalendarDay)).Should().Be(1);
            (await verify.ProactiveCalendarDays.SingleAsync()).TimedEventCount.Should().Be(4);
        }
    }
}
